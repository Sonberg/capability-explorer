import assert from "node:assert/strict";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const root = resolve(import.meta.dirname, "..");
const client = resolve(root, "platform/src/CapabilityExplorer.Explorer/client");
const { createServer } = await import(pathToFileURL(resolve(client, "node_modules/vite/dist/node/index.js")).href);
const { createElement } = await import(pathToFileURL(resolve(client, "node_modules/react/index.js")).href);
const { renderToStaticMarkup } = await import(pathToFileURL(resolve(client, "node_modules/react-dom/server.node.js")).href);
const server = await createServer({
  configFile: resolve(client, "vite.config.ts"),
  root: client,
  server: { middlewareMode: true },
  appType: "custom",
  logLevel: "silent"
});

try {
  const { DmnTables, Diagnostics, Provenance, groupModelDiagnostics } = await server.ssrLoadModule("/src/views/ModelEvidence.tsx");
  const { modelSelectionUpdates } = await server.ssrLoadModule("/src/views/model-view-data.ts");
  const namespace = "https://www.omg.org/spec/DMN/20191111/MODEL/";
  const elements = [
    { identity: "table", kind: "dmn", namespace: "urn:dmn", id: "DecisionTable", type: "dmn:decisionTable", order: 0, attributes: { name: "Eligibility" }, hitPolicy: "FIRST", source: { artifact: "model.dmn" } },
    { identity: "input", kind: "dmn", namespace: "urn:dmn", id: "Input_Age", type: "dmn:input", order: 1, parent: "table", attributes: { label: "Age" }, source: { artifact: "model.dmn", element: "Input_Age" } },
    { identity: "input-values", kind: "dmn", namespace: "urn:dmn", id: "InputValues_Age", type: "dmn:inputValues", order: 2, parent: "input", attributes: {}, content: { text: "<18, [18..24], -" }, source: { artifact: "model.dmn", element: "InputValues_Age" } },
    { identity: "output", kind: "dmn", namespace: "urn:dmn", id: "Output_Eligibility", type: "dmn:output", order: 3, parent: "table", attributes: { label: "Eligibility" }, source: { artifact: "model.dmn", element: "Output_Eligibility" } },
    { identity: "output-values", kind: "dmn", namespace: "urn:dmn", id: "OutputValues_Eligibility", type: "dmn:outputValues", order: 4, parent: "output", attributes: {}, content: { text: '"deny", "approve"' }, source: { artifact: "model.dmn", element: "OutputValues_Eligibility" } },
    { identity: "rule", kind: "dmn", namespace: "urn:dmn", id: "Rule_1", type: "dmn:rule", order: 5, parent: "table", attributes: {}, cells: [{ kind: "input", order: 0, text: "<18", language: "FEEL" }, { kind: "output", order: 1, text: '"deny"', language: "FEEL" }], source: { artifact: "model.dmn", element: "Rule_1" } }
  ].map((element) => ({ ...element, namespace }));
  const tableMarkup = renderToStaticMarkup(createElement(DmnTables, {
    elements, selected: "", onSelect: () => undefined
  }));
  const columns = [...tableMarkup.matchAll(/<th\b[^>]*>([\s\S]*?)<\/th>/g)].map((match) => match[1]);
  assert.equal(columns.length, 2);
  assert.match(columns[0], /Age/);
  assert.match(columns[0], /allowed values/i);
  assert.match(columns[0], /&lt;18, \[18\.\.24\], -/);
  assert.doesNotMatch(columns[0], /deny|approve/);
  assert.match(columns[1], /Eligibility/);
  assert.match(columns[1], /allowed values/i);
  assert.match(columns[1], /&quot;deny&quot;, &quot;approve&quot;/);
  assert.doesNotMatch(columns[1], /&lt;18/);
  assert.match(tableMarkup, /Hit policy: FIRST/);
  assert.match(tableMarkup, /literal, not evaluated/);
  assert.match(tableMarkup, /&quot;deny&quot;/);

  const missingValueMarkup = renderToStaticMarkup(createElement(DmnTables, {
    elements: elements.filter((element) => element.identity !== "input-values"),
    selected: "", onSelect: () => undefined
  }));
  const missingInputColumn = [...missingValueMarkup.matchAll(/<th\b[^>]*>([\s\S]*?)<\/th>/g)][0]?.[1] ?? "";
  assert.match(missingInputColumn, /No standard DMN allowed-value declaration is present in this evidence/);
  assert.match(missingInputColumn, /The reason is not established by this view/);
  assert.doesNotMatch(missingInputColumn, /&lt;18/);

  const inputColumnFor = (values: typeof elements) => {
    const markup = renderToStaticMarkup(createElement(DmnTables, {
      elements: values, selected: "", onSelect: () => undefined
    }));
    return [...markup.matchAll(/<th\b[^>]*>([\s\S]*?)<\/th>/g)][0]?.[1] ?? "";
  };
  for (const content of [undefined, { text: "" }]) {
    const column = inputColumnFor(elements.map((element) => element.identity === "input-values"
      ? { ...element, content, attributes: { text: "untrusted attribute is not a declaration" } }
      : element));
    assert.match(column, /Declaration present; its text is empty or was not captured/);
    assert.match(column, /The reason is not established by this view/);
    assert.doesNotMatch(column, /untrusted attribute|No standard DMN/);
  }
  const vendorColumn = inputColumnFor(elements.map((element) => element.identity === "input-values"
    ? { ...element, namespace: "urn:vendor", type: "vendor:inputValues", content: { text: "vendor-only values" } }
    : element));
  assert.match(vendorColumn, /No standard DMN allowed-value declaration is present in this evidence/);
  assert.doesNotMatch(vendorColumn, /vendor-only values/);

  const longArtifact = `models/${"nested/".repeat(90)}eligibility.dmn`;
  const diagnostics = [
    { code: "DMN_TEXT_MISSING", message: "Input values missing on Input_Age", disposition: "partial", source: { artifact: longArtifact, element: "Input_Age" } },
    { code: "DMN_TEXT_MISSING", message: "Input values missing on Input_Other", disposition: "partial", source: { artifact: longArtifact, element: "Input_Other" } },
    { code: "BPMN_EXTENSION_UNKNOWN", message: "Extension retained as opaque", disposition: "partial", source: { artifact: "model.bpmn", element: "Task_Vendor" } },
    { code: "UNLOCATED", message: "<script>Source id missing</script>" }
  ];
  const diagnosticElements = [
    { identity: "input-a", kind: "dmn", namespace: "urn:dmn", id: "Input_Age", type: "dmn:input", order: 0, attributes: {}, source: { artifact: longArtifact, element: "Input_Age" } },
    { identity: "input-b", kind: "dmn", namespace: "urn:dmn", id: "Input_Other", type: "dmn:input", order: 1, attributes: {}, source: { artifact: longArtifact, element: "Input_Other" } },
    { identity: "task", kind: "bpmn", namespace: "urn:bpmn", id: "Task_Vendor", type: "bpmn:task", order: 2, attributes: {}, source: { artifact: "model.bpmn", element: "Task_Vendor" } }
  ];
  const diagnosticsMarkup = renderToStaticMarkup(createElement(Diagnostics, {
    diagnostics, elements: diagnosticElements
  }));
  const groupHeaders = groupModelDiagnostics(diagnostics, diagnosticElements)
    .map(({ code, disposition, construct, items }) => [code, disposition, construct, items.length]);
  assert.deepEqual(groupHeaders, groupModelDiagnostics([...diagnostics].reverse(), diagnosticElements)
    .map(({ code, disposition, construct, items }) => [code, disposition, construct, items.length]));
  const diagnosticsText = diagnosticsMarkup.replace(/<[^>]+>/g, "");
  assert.match(diagnosticsMarkup, /Diagnostics \(4\)/);
  assert.match(diagnosticsText, /DMN_TEXT_MISSING · partial · urn:dmn · dmn:input · 2 occurrences/);
  assert.match(diagnosticsText, /BPMN_EXTENSION_UNKNOWN · partial · urn:bpmn · bpmn:task · 1 occurrence/);
  assert.match(diagnosticsText, /UNLOCATED · not specified · Construct not identified · 1 occurrence/);
  assert.equal([...diagnosticsMarkup.matchAll(/<details\b/g)].length, 3);
  assert.equal([...diagnosticsMarkup.matchAll(/<details\b[^>]*\bopen(?:=|\s|>)/g)].length, 0);
  for (const exactOccurrence of [
    "Input values missing on Input_Age", "Input values missing on Input_Other",
    "Input_Age", "Input_Other", "Task_Vendor", longArtifact
  ]) assert(diagnosticsMarkup.includes(exactOccurrence), `missing diagnostic occurrence data: ${exactOccurrence}`);
  assert.match(diagnosticsMarkup, /Source locator not supplied/);
  assert.match(diagnosticsMarkup, /break-all/);
  assert.match(diagnosticsMarkup, /&lt;script&gt;Source id missing&lt;\/script&gt;/);
  assert.doesNotMatch(diagnosticsMarkup, /<script>/);

  const deniedProvenance = renderToStaticMarkup(createElement(Provenance, {
    summary: {
      id: "denied-model", kind: "authored", repositoryId: "repo-a", commitSha: "a".repeat(40),
      unitPath: "models/eligibility.dmn", modelKind: "dmn", coverage: "partial", diagnosticCount: 0,
      source: { status: "denied" }
    },
    selectedElement: "", sourceVerified: false, renderStatus: ""
  }));
  assert.match(deniedProvenance, /original source not verified \(denied\)/);
  assert.match(deniedProvenance, /Source access[\s\S]*denied · source bytes not verified/);
  assert.deepEqual(modelSelectionUpdates("model-dmn"), { id: "model-dmn", element: undefined });

  console.log("Model UI presentation cases passed: DMN value/column association, literal text, missing declarations, grouped diagnostics and full locators.");
} finally {
  await server.close();
}
