import { createHash } from 'node:crypto';

export const compareUtf8 = (left, right) => Buffer.compare(Buffer.from(left, 'utf8'), Buffer.from(right, 'utf8'));
export const canonical = value => JSON.stringify(Array.isArray(value) ? value.map(item => JSON.parse(canonical(item))) :
  value && typeof value === 'object'
    ? Object.fromEntries(Object.keys(value).sort(compareUtf8).map(key => [key, JSON.parse(canonical(value[key]))]))
    : value);

const schemaKeywords = new Set([
  '$schema', '$id', 'title', 'description', 'type', 'const', 'enum', 'pattern',
  'minLength', 'maxLength', 'minimum', 'required', 'properties',
  'additionalProperties', 'items', 'minItems', 'uniqueItems',
]);
const featureRegistry = new Set(['declarations', 'references', 'source-spans', 'workspace-links']);
const unitIdPattern = /^[A-Za-z0-9._-]{1,128}$/;
const ownerDepth = unit => unit.root === '.' ? 0 : unit.root.split('/').length;

export function validateSchemaDefinition(schema, path = '$schema') {
  const errors = Object.keys(schema).filter(key => !schemaKeywords.has(key))
    .map(key => `${path}: unsupported schema keyword ${key}`);
  for (const [key, nested] of Object.entries(schema.properties ?? {})) {
    errors.push(...validateSchemaDefinition(nested, `${path}.properties.${key}`));
  }
  if (schema.items) errors.push(...validateSchemaDefinition(schema.items, `${path}.items`));
  return errors;
}

export function validateInstance(value, schema, path = '$') {
  const errors = path === '$' ? validateSchemaDefinition(schema) : [];
  const types = {
    object: value !== null && typeof value === 'object' && !Array.isArray(value),
    array: Array.isArray(value), string: typeof value === 'string',
    integer: Number.isInteger(value), number: typeof value === 'number' && Number.isFinite(value),
    boolean: typeof value === 'boolean', null: value === null,
  };
  if (schema.type && !types[schema.type]) return [`${path}: expected ${schema.type}`];
  if ('const' in schema && value !== schema.const) errors.push(`${path}: const mismatch`);
  if (schema.enum && !schema.enum.includes(value)) errors.push(`${path}: enum mismatch`);
  if (schema.pattern && (typeof value !== 'string' || !new RegExp(schema.pattern).test(value))) errors.push(`${path}: pattern mismatch`);
  if (typeof value === 'string' && (value.length < (schema.minLength ?? 0) || value.length > (schema.maxLength ?? Infinity))) errors.push(`${path}: string length outside bounds`);
  if (typeof value === 'number' && value < (schema.minimum ?? -Infinity)) errors.push(`${path}: below minimum`);
  if (Array.isArray(value)) {
    if (value.length < (schema.minItems ?? 0)) errors.push(`${path}: too few items`);
    if (schema.uniqueItems && new Set(value.map(canonical)).size !== value.length) errors.push(`${path}: duplicate items`);
    if (schema.items) value.forEach((item, index) => errors.push(...validateInstance(item, schema.items, `${path}[${index}]`)));
  }
  if (types.object) {
    for (const required of schema.required ?? []) if (!(required in value)) errors.push(`${path}: missing ${required}`);
    for (const [key, nested] of Object.entries(value)) {
      if (schema.properties?.[key]) errors.push(...validateInstance(nested, schema.properties[key], `${path}.${key}`));
      else if (schema.additionalProperties === false) errors.push(`${path}: unknown property ${key}`);
    }
  }
  return errors;
}

export function isRepoPath(path, allowRoot = false) {
  if (typeof path !== 'string' || path.length === 0 || path.includes('\0') || path.includes('\\') ||
      path.includes(':') || path.startsWith('/') || /^[A-Za-z]:/.test(path) ||
      /^[A-Za-z][A-Za-z0-9+.-]*:\/\//.test(path)) return false;
  if (allowRoot && path === '.') return true;
  return !path.endsWith('/') && !path.includes('//') &&
    path.split('/').every(segment => segment !== '' && segment !== '.' && segment !== '..');
}

export function parseUnitKey(key) {
  const match = typeof key === 'string' && /^unit:v1\/(.+)#([A-Za-z0-9._-]{1,128})$/.exec(key);
  if (!match || !isRepoPath(match[1], true) || !unitIdPattern.test(match[2])) return null;
  return { ownerDirectory: match[1], declaredUnitId: match[2] };
}

function validateReferenceTarget(value) {
  const separator = typeof value === 'string' ? value.lastIndexOf('#') : -1;
  if (separator <= 0 || !isRepoPath(value.slice(0, separator), true) ||
      !unitIdPattern.test(value.slice(separator + 1))) return false;
  return true;
}

export function validateAdapter(value, schema, sourceBytes = {}) {
  const errors = validateInstance(value, schema);
  if (errors.length) return errors;
  const { request, result } = value;
  const supported = new Set(result.features.supported);
  const unsupported = new Set(result.features.unsupported);
  const knownLanguages = new Set(['csharp', 'typescript', 'javascript', 'sql', 'config']);
  const unit = request.unit;
  if (!isRepoPath(unit.path, true)) errors.push('unit path is not normalized repository-relative POSIX path');
  if (!unitIdPattern.test(unit.name) || unit.unitKey !== `unit:v1/${unit.path}#${unit.name}`) errors.push('unit identity disagrees with owner path and declared id');
  if (request.profileId !== result.languageProfileId) errors.push('requested and produced language profiles differ');
  if ([...supported].some(feature => unsupported.has(feature))) errors.push('feature is both supported and unsupported');
  for (const feature of supported) if (!featureRegistry.has(feature)) errors.push(`unknown supported capability ${feature}`);
  for (const feature of request.requestedFeatures) {
    if (!supported.has(feature) && !unsupported.has(feature)) errors.push(`unreported requested capability ${feature}`);
    if (!featureRegistry.has(feature) && (!unsupported.has(feature) || !['unsupported', 'partial'].includes(result.status))) {
      errors.push(`unknown capability ${feature} was not rejected explicitly`);
    }
  }
  for (const feature of unsupported) if (!featureRegistry.has(feature) && !request.requestedFeatures.includes(feature)) {
    errors.push(`unrequested unknown capability ${feature}`);
  }
  if ((!knownLanguages.has(request.profileId) || !knownLanguages.has(result.languageProfileId)) &&
      result.status !== 'unsupported') errors.push('unknown profile is not reported unsupported');
  if (['unsupported', 'failed'].includes(result.status) && (supported.size || result.facts.length)) {
    errors.push(`${result.status} result contains semantic claims`);
  }
  if (result.status === 'unsupported' && request.requestedFeatures.some(feature => !unsupported.has(feature))) {
    errors.push('unsupported result does not account for every requested feature');
  }
  if (result.coverage.filesAnalyzed > result.coverage.filesTotal) errors.push('analyzed files exceed total');
  if (result.coverage.unresolvedFacts !== result.facts.filter(fact => ['candidate', 'unresolved'].includes(fact.resolution)).length) {
    errors.push('unresolved fact coverage count mismatch');
  }
  if (result.status === 'complete' && (result.coverage.filesAnalyzed !== result.coverage.filesTotal ||
      result.coverage.omittedPaths.length || unsupported.size || result.coverage.unresolvedFacts ||
      result.coverage.diagnostics.length)) errors.push('complete result hides incomplete coverage or diagnostics');
  const factIds = new Set(result.facts.map(fact => fact.factId));
  if (factIds.size !== result.facts.length) errors.push('duplicate fact identity');
  for (const fact of result.facts) {
    if (['resolved', 'static-target'].includes(fact.resolution) &&
        (!fact.targetFactId || !factIds.has(fact.targetFactId))) errors.push(`unlinked ${fact.resolution} fact`);
    for (const span of fact.evidence) {
      if (!isRepoPath(span.path)) errors.push(`evidence path is not normalized repository-relative POSIX path`);
      if (span.endByte <= span.startByte) errors.push(`invalid source byte span for ${fact.factId}`);
      const source = sourceBytes[span.path];
      if (source) {
        const bytes = Buffer.from(source);
        const digest = createHash('sha256').update(bytes).digest('hex');
        if (digest !== span.sourceSha256 || span.endByte > bytes.length) errors.push(`source byte span/digest exceeds frozen bytes for ${fact.factId}`);
      }
    }
  }
  for (const path of result.coverage.omittedPaths) if (!isRepoPath(path)) errors.push('omitted path is not normalized repository-relative POSIX path');
  return errors;
}

export function validateDiscovery(value, schema, scenario, expected, registry) {
  const errors = validateInstance(value, schema);
  if (errors.length) return errors;
  const keys = new Set(value.units.map(unit => unit.unitKey));
  if (keys.size !== value.units.length) errors.push('duplicate unit identity');
  if (value.coverage.unitsAccepted !== value.units.length) errors.push('unit coverage count mismatch');
  const files = value.units.flatMap(unit => unit.files);
  if (value.coverage.filesIncluded !== files.length) errors.push('file coverage count mismatch');
  if (new Set(files).size !== files.length) errors.push('file has conflicting owners');
  if (value.units.some(unit => unit.capabilities.some(capability => !registry.supportedCapabilities.includes(capability)))) errors.push('unknown capability');
  if (value.units.some(unit => !registry.supportedLanguages.includes(unit.languageProfileId))) errors.push('unknown language profile');
  const overBudget = scenario.units.length > scenario.budgets.maxCandidates ||
    scenario.entries.some(entry => entry.path.split('/').length - 1 > scenario.budgets.maxDepth) ||
    scenario.entries.filter(entry => ['source', 'test', 'generated'].includes(entry.kind)).length > scenario.budgets.maxFiles ||
    scenario.entries.reduce((sum, entry) => sum + entry.bytes, 0) > scenario.budgets.maxBytes ||
    value.units.some(unit => unit.files.length > value.policy.maxFilesPerUnit);
  if (overBudget && (value.status !== 'failed' || !value.coverage.diagnostics.includes('limit-exceeded'))) errors.push('budget failure is hidden');
  if (value.status !== expected.status) errors.push('inventory status differs from sealed expectation');
  const actualInventory = value.units.map(unit => ({
    unitKey: unit.unitKey, languageProfileId: unit.languageProfileId, files: unit.files, references: unit.references,
  }));
  if (canonical(actualInventory) !== canonical(expected.units)) errors.push('inventory differs from sealed expectation');
  if (canonical(value.coverage.pathsExcluded) !== canonical(expected.excluded.map(item => item.path))) errors.push('exclusions differ from sealed expectation');
  if (canonical(value.coverage.diagnostics) !== canonical(expected.diagnostics)) errors.push('diagnostics differ from sealed expectation');
  for (const unit of value.units) {
    if (!isRepoPath(unit.ownerDirectory, true) || !isRepoPath(unit.markerPath) ||
        unit.unitKey !== `unit:v1/${unit.ownerDirectory}#${unit.declaredUnitId}`) errors.push('unit key/owner/marker identity is unsafe or inconsistent');
    for (const file of unit.files) if (!isRepoPath(file)) errors.push('unit file path is not normalized repository-relative POSIX path');
    for (const reference of unit.references) if (!parseUnitKey(reference.targetUnitKey)) errors.push('reference target is not a normalized unit key');
  }
  return errors;
}

export function validAdapter() {
  const source = Buffer.from('éx', 'utf8');
  const sourceSha256 = createHash('sha256').update(source).digest('hex');
  const evidence = [{
    path: 'web/src/index.ts', sourceSha256, startByte: 0, endByte: 2,
    byteOffsetEncoding: 'source-bytes',
    position: { encoding: 'utf-16-code-units', startLine: 0, startCharacter: 0, endLine: 0, endCharacter: 1 },
  }];
  return {
    contract: 'ce.frontend.contractDraft.v1',
    request: {
      sourceSnapshot: { repositoryId: 'sample-repo', revisionSha: 'b'.repeat(40), snapshotSha256: 'a'.repeat(64) },
      unit: { unitKey: 'unit:v1/web#web', path: 'web', name: 'web' },
      profileId: 'typescript', requestedFeatures: ['declarations', 'references', 'source-spans'],
    },
    result: {
      producer: { name: 'fixture-producer', version: '1.0', executableSha256: 'a'.repeat(64), configurationSha256: 'a'.repeat(64) },
      languageProfileId: 'typescript',
      features: { supported: ['declarations', 'references', 'source-spans'], unsupported: [] },
      facts: [
        { factId: 'symbol:target', kind: 'declaration', resolution: 'not-applicable', evidence },
        { factId: 'reference:call', kind: 'reference', resolution: 'resolved', targetFactId: 'symbol:target', evidence },
      ],
      coverage: { filesTotal: 1, filesAnalyzed: 1, omittedPaths: [], unresolvedFacts: 0, diagnostics: [] },
      status: 'complete',
    },
  };
}

export function validDiscovery(scenario, expected) {
  return {
    contract: 'ce.discovery.contractDraft.v1',
    sourceSnapshot: { repositoryId: 'sample-repo', revisionSha: 'b'.repeat(40), snapshotSha256: 'a'.repeat(64) },
    policy: {
      registryId: 'fixture-policy', registryVersion: '1.0.0', ...scenario.budgets, includeTests: 'exclude',
    },
    units: expected.units.map(unit => {
      const source = scenario.units.find(candidate => `unit:v1/${candidate.root}#${candidate.id}` === unit.unitKey);
      return {
        ...unit, ownerDirectory: source.root, markerPath: source.markerPath, declaredUnitId: source.id,
        capabilities: source.capabilities,
      };
    }),
    coverage: {
      candidateMarkers: scenario.entries.filter(entry => entry.kind === 'marker').length,
      unitsAccepted: expected.units.length,
      filesIncluded: expected.units.reduce((count, unit) => count + unit.files.length, 0),
      pathsExcluded: expected.excluded.map(item => item.path),
      diagnostics: expected.diagnostics,
    },
    status: expected.status,
  };
}

function validGlob(pattern) {
  if (typeof pattern !== 'string' || !pattern || pattern.includes('\0') || pattern.includes('\\') ||
      pattern.includes(':') || pattern.startsWith('/') || pattern.includes('//') ||
      [...pattern].some(char => '?[]{}'.includes(char))) return false;
  const segments = pattern.split('/');
  return segments.every(segment => segment !== '' && segment !== '.' && segment !== '..' &&
    (!segment.includes('**') || segment === '**'));
}

export function matchesGlob(pattern, path) {
  if (!validGlob(pattern)) throw new Error(`unsupported glob pattern: ${pattern}`);
  if (!isRepoPath(path)) return false;
  const patternSegments = pattern.split('/');
  const pathSegments = path.split('/');
  const memo = new Map();
  const matches = (pi, si) => {
    const key = `${pi}:${si}`;
    if (memo.has(key)) return memo.get(key);
    let result;
    if (pi === patternSegments.length) result = si === pathSegments.length;
    else if (patternSegments[pi] === '**') {
      result = matches(pi + 1, si) || (si < pathSegments.length && matches(pi, si + 1));
    } else if (si < pathSegments.length) {
      const re = new RegExp(`^${patternSegments[pi].split('*').map(part => part.replace(/[|\\{}()[\]^$+?.]/g, '\\$&')).join('[^/]*')}$`);
      result = re.test(pathSegments[si]) && matches(pi + 1, si + 1);
    } else result = false;
    memo.set(key, result);
    return result;
  };
  return matches(0, 0);
}

export function evaluateScenario(scenario, registry) {
  const fail = (status, diagnostic) => ({ scenarioId: scenario.id, status, units: [], excluded: [], diagnostics: [diagnostic] });
  const allPaths = scenario.entries.map(entry => entry.path);
  if (allPaths.some(path => !isRepoPath(path))) return fail('failed', 'path-escape');
  for (const unit of scenario.units) {
    if (!isRepoPath(unit.root, true) || !isRepoPath(unit.markerPath) ||
        !unitIdPattern.test(unit.id) || unit.unitKey && unit.unitKey !== `unit:v1/${unit.root}#${unit.id}`) {
      return fail('failed', 'unit-identity-mismatch');
    }
    if (unit.references.some(reference => !validateReferenceTarget(reference.target))) return fail('failed', 'path-escape');
    try {
      for (const pattern of [...unit.includePatterns, ...(unit.excludePatterns ?? [])]) if (!validGlob(pattern)) throw new Error();
    } catch { return fail('failed', 'unsupported-glob-pattern'); }
  }
  if (scenario.entries.some(entry => entry.kind === 'symlink')) return fail('failed', 'symlink-escape');
  const unknownLanguage = scenario.units.some(unit => !registry.supportedLanguages.includes(unit.language));
  if (unknownLanguage) return fail('unsupported', 'unknown-language-profile');
  if (scenario.units.some(unit => unit.capabilities.some(item => !registry.supportedCapabilities.includes(item)))) {
    return fail('unsupported', 'unknown-capability');
  }
  const { budgets } = scenario;
  const candidateFiles = scenario.entries.filter(entry => ['source', 'test', 'generated'].includes(entry.kind));
  const markerCount = scenario.entries.filter(entry => entry.kind === 'marker').length;
  if (scenario.units.length > budgets.maxCandidates || markerCount > budgets.maxCandidates ||
      scenario.entries.some(entry => entry.path.split('/').length - 1 > budgets.maxDepth) ||
      candidateFiles.length > budgets.maxFiles ||
      scenario.entries.reduce((sum, entry) => sum + entry.bytes, 0) > budgets.maxBytes) return fail('failed', 'limit-exceeded');

  const units = scenario.units.map(unit => ({ ...unit, key: `unit:v1/${unit.root}#${unit.id}` }));
  const identities = new Map();
  for (const unit of units) {
    const identity = `${unit.root}#${unit.id}`;
    if (identities.has(identity) && identities.get(identity) !== unit.language) return fail('failed', 'conflicting-unit-identity');
    identities.set(identity, unit.language);
  }
  const nestedRoots = scenario.entries.filter(entry => entry.kind === 'nested-repository').map(entry => entry.path.replace(/\/\.git$/, ''));
  const owners = new Map();
  const excluded = new Map();
  let deduplicated = false;
  for (const entry of scenario.entries) {
    if (nestedRoots.some(nestedRoot => entry.path === nestedRoot || entry.path.startsWith(`${nestedRoot}/`))) {
      excluded.set(entry.path, 'nested-repository-boundary');
    }
  }
  for (const entry of candidateFiles) {
    const path = entry.path;
    if (nestedRoots.some(nestedRoot => path === nestedRoot || path.startsWith(`${nestedRoot}/`))) {
      excluded.set(path, 'nested-repository-boundary');
      continue;
    }
    if (entry.kind === 'test' || entry.kind === 'generated') {
      excluded.set(path, entry.kind === 'test' ? 'test-policy-exclude' : 'generated');
      continue;
    }
    const matches = [];
    let explicitlyExcluded = false;
    for (const unit of units) {
      const prefix = unit.root === '.' ? '' : `${unit.root}/`;
      if (!path.startsWith(prefix)) continue;
      const relative = path.slice(prefix.length);
      const included = unit.includePatterns.filter(pattern => matchesGlob(pattern, relative)).length;
      const excludedByRule = (unit.excludePatterns ?? []).some(pattern => matchesGlob(pattern, relative));
      if (included > 1 && !excludedByRule) deduplicated = true;
      if (included && excludedByRule) explicitlyExcluded = true;
      else if (included) matches.push(unit);
    }
    if (explicitlyExcluded) {
      excluded.set(path, 'explicit-exclude');
      continue;
    }
    matches.sort((a, b) => ownerDepth(b) - ownerDepth(a) || compareUtf8(a.key, b.key));
    if (matches.length) {
      const owner = matches[0];
      if (matches.some(match => ownerDepth(match) === ownerDepth(owner) && match.key !== owner.key)) {
        return fail('failed', 'ownership-conflict');
      }
      owners.set(path, owner);
    }
  }
  const grouped = new Map(units.map(unit => [unit.key, new Set()]));
  for (const [path, owner] of owners) grouped.get(owner.key).add(path);
  if (units.some(unit => grouped.get(unit.key).size > budgets.maxFilesPerUnit)) return fail('failed', 'limit-exceeded');
  if (new Set(owners.keys()).size > budgets.maxFiles) return fail('failed', 'limit-exceeded');
  const inventory = units.map(unit => ({
    unitKey: unit.key, languageProfileId: unit.language,
    files: [...grouped.get(unit.key)].sort(compareUtf8),
    references: unit.references.map(reference => {
      const target = `unit:v1/${reference.target}`;
      return {
        targetUnitKey: target, relation: reference.relation,
        status: units.some(candidate => candidate.key === target) ? 'resolved' : 'missing-dependency',
      };
    }).sort((a, b) => compareUtf8(a.targetUnitKey, b.targetUnitKey) || compareUtf8(a.relation, b.relation)),
  })).sort((a, b) => compareUtf8(a.unitKey, b.unitKey));
  const diagnostics = [];
  const markerPaths = scenario.entries.filter(entry => entry.kind === 'marker').map(entry => entry.path);
  const packageDirs = markerPaths.filter(path => path.endsWith('/package.json')).map(path => path.slice(0, -'/package.json'.length));
  if (markerPaths.some(path => path === 'package.json') && markerPaths.includes('tsconfig.json') ||
      packageDirs.some(directory => markerPaths.includes(`${directory}/tsconfig.json`))) {
    diagnostics.push('package-context-under-tsconfig');
  }
  if (nestedRoots.length) diagnostics.push('nested-repository-boundary');
  if (deduplicated) diagnostics.push('same-owner-path-deduplicated');
  if (inventory.some(unit => unit.references.some(reference => reference.status === 'missing-dependency'))) diagnostics.push('missing-dependency');
  if (!units.length && candidateFiles.some(entry => entry.kind === 'source')) diagnostics.push('marker-free-source-ambiguous');
  const status = diagnostics.includes('missing-dependency') ? 'partial' :
    diagnostics.includes('marker-free-source-ambiguous') ? 'ambiguous' : 'complete';
  return {
    scenarioId: scenario.id, status, units: inventory,
    excluded: [...excluded].map(([path, reason]) => ({ path, reason })).sort((a, b) => compareUtf8(a.path, b.path)),
    diagnostics: diagnostics.sort(compareUtf8),
  };
}
