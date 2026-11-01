import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { canonical, equalRuns, hash, requireThat, verifyEvidence } from './mapping-replay-lib.mjs';
import { readJson, safePath, semanticDelta, validatePlan } from './corpus-replay-lib.mjs';
const root = process.cwd();
const save = (path, value) => writeFileSync(path, canonical(value) + '\n');
const runnerHash = () => hash(canonical(Object.fromEntries(['scripts/corpus-replay','scripts/corpus-replay.mjs','scripts/corpus-replay-lib.mjs','scripts/mapping-replay-lib.mjs','scripts/sandbox'].map(p => [p,hash(readFileSync(safePath(root,p)))]))));
async function collect(plan, repo, lane, repeat) {
  const { validateArtifact } = await import(pathToFileURL(safePath(root, `${plan.builds.candidate.path}/ts/extractors/ts/src/contracts/validateArtifact.ts`)));
  const result = { hashes: {}, units: {}, evidence: 0 };
  for (const command of repo.commands) {
    const directory = safePath(root, `${plan.output}/${lane}/${repeat}/${repo.id}/${command}`);
    const files = readdirSync(directory).sort();
    requireThat(files.length > 0 && files.every((f) => f.endsWith('.jsonl')), 'missing or unexpected artifact output');
    for (const file of files) {
      const bytes = readFileSync(safePath(root, `${plan.output}/${lane}/${repeat}/${repo.id}/${command}/${file}`));
      const checked = validateArtifact(bytes.toString());
      requireThat(checked.ok, 'artifact schema/structure failed');
      const records = bytes.toString().trimEnd().split('\n').map(JSON.parse), header = records[0];
      const unit = `${command}:${header.unit.path}`;
      requireThat(!Object.hasOwn(result.units, unit), 'duplicate artifact unit');
      result.units[unit] = records; result.hashes[`${command}/${file}`] = hash(bytes);
      result.evidence += verifyEvidence(records, safePath(root, repo.source), { repositoryId: repo.id, commitSha: repo.commit, files: repo.files });
    }
  }
  return result;
}
async function main() {
  const [command, name] = process.argv.slice(2);
  requireThat(process.argv.length === 4 && ['validate-plan','prepare-run','verify'].includes(command), 'usage: corpus-replay validate-plan|run <local-plan>');
  const plan = readJson(safePath(root,name)), validated = validatePlan(root,plan);
  if (command === 'validate-plan') { console.log(`validated ${plan.repositories.length} locked repositories; no extraction performed`); return; }
  if (command === 'prepare-run') {
    requireThat(!existsSync(validated.output), 'output exists; select a fresh immutable run path');
    mkdirSync(validated.output, {recursive:true});
    save(resolve(validated.output,'plan-receipt.json'), {planHash:validated.planHash, runnerHash:runnerHash(), images:plan.images, builds:Object.fromEntries(Object.entries(validated.builds).map(([k,v])=>[k,v.buildHash]))});
    const commands = [];
    for (const lane of ['baseline','candidate']) for (const repeat of [1,2]) for (const repo of plan.repositories) for (const cmd of repo.commands) {
      const dest = `${plan.output}/${lane}/${repeat}/${repo.id}/${cmd}`;
      mkdirSync(safePath(root,dest), {recursive:true});
      commands.push([cmd,plan.images[cmd === 'csharp' ? 'dotnet' : 'node'],plan.builds[lane].path,repo.source,repo.id,repo.commit,dest,repo.dependencies?.packages ?? '-',repo.dependencies?.lock ?? '-'].join('\t'));
    }
    writeFileSync(resolve(validated.output,'commands.tsv'),commands.join('\n')+'\n');
    console.log(plan.output); return;
  }
  const receipt = readJson(resolve(validated.output,'plan-receipt.json'));
  requireThat(receipt.planHash === validated.planHash && receipt.runnerHash === runnerHash(), 'plan or runner changed during run');
  const repositories = [];
  for (const repo of plan.repositories) {
    const before = await collect(plan,repo,'baseline',1), before2 = await collect(plan,repo,'baseline',2);
    const after = await collect(plan,repo,'candidate',1), after2 = await collect(plan,repo,'candidate',2);
    equalRuns(before.hashes,before2.hashes); equalRuns(after.hashes,after2.hashes);
    const units = [...new Set([...Object.keys(before.units),...Object.keys(after.units)])].sort().map((unit) => {
      const delta = semanticDelta(before.units[unit] ?? [],after.units[unit] ?? []);
      return {unit, ...delta};
    });
    repositories.push({id:repo.id,commit:repo.commit,inputHash:repo.inputHash,baselineArtifacts:before.hashes,candidateArtifacts:after.hashes,evidenceSpans:before.evidence+after.evidence,units});
  }
  save(resolve(validated.output,'report.json'),{format:'corpus-replay-report.v1',...receipt,repeatBytesEqual:true,repositories,
    limitations:['Local explicit-input receipt, not signed provenance or quality acceptance.','Semantic deltas require human review; names alone do not prove binding equivalence.','Private source, paths and artifact contents remain in ignored local output.','No analyzed repository was built, restored or executed; framework packs are bounded by pinned runtime image.']});
  console.log(`replay verified: ${repositories.length} repositories, deterministic repeats; private report retained locally`);
}
try { await main(); } catch(error) { console.error(`corpus replay failed: ${error.message}`); process.exitCode=1; }
