type FixtureInput = {
  bpmnHash: string;
  dmnHash: string;
  revisionA: string;
  revisionB: string;
  noDiHash: string;
  bpmnXml: string;
  dmnXml: string;
};

type SummaryFixture = {
  id: string;
  kind: "authored" | "candidate";
  repositoryId: string;
  commitSha: string;
  unitPath: string;
  modelKind: "bpmn" | "dmn" | "candidate";
  coverage: "full" | "partial";
  diagnosticCount: number;
  source: { status: "available" | "missing" | "denied"; sha256: string };
};

export function createUiModelEvidence({ bpmnHash, dmnHash, revisionA, revisionB, noDiHash, bpmnXml, dmnXml }: FixtureInput) {
  const bpmnIdentity = `model:${"1".repeat(64)}`;
  const laneIdentity = `model:${"a".repeat(64)}`;
  const startIdentity = `model:${"b".repeat(64)}`;
  const serviceIdentity = `model:${"c".repeat(64)}`;
  const subprocessIdentity = `model:${"d".repeat(64)}`;
  const nestedTaskIdentity = `model:${"e".repeat(64)}`;
  const taskIdentity = `model:${"7".repeat(64)}`;
  const dmnIdentity = `model:${"2".repeat(64)}`;
  const tableIdentity = `model:${"3".repeat(64)}`;
  const ruleIdentities = [`model:${"4".repeat(64)}`, `model:${"5".repeat(64)}`, `model:${"6".repeat(64)}`];
  const sourceLocator = { artifact: "model.xml", element: "Decision_Eligibility" };
  const bpmnEvidence = {
    schema: "ce.models.v1",
    repositoryId: "repo-a",
    commitSha: revisionA,
    unitPath: "models/application.bpmn.json",
    sourceSha256: bpmnHash,
    contentProfile: "model-content",
    coverage: { status: "partial", supported: ["BPMN process"], limitations: ["Vendor extensions are opaque."] },
    diagnostics: [{ code: "MODEL_PARTIAL", message: "One opaque extension was retained.", disposition: "partial", source: sourceLocator }],
    elements: [
      { identity: bpmnIdentity, kind: "bpmn", namespace: "http://www.omg.org/spec/BPMN/20100524/MODEL", id: "Process_A", type: "bpmn:process", order: 0, attributes: { name: "Application" }, source: sourceLocator },
      { identity: laneIdentity, kind: "bpmn", namespace: "http://www.omg.org/spec/BPMN/20100524/MODEL", id: "Lane_Main", type: "bpmn:lane", order: 1, parent: bpmnIdentity, attributes: { name: "Applicant lane" }, source: sourceLocator },
      { identity: startIdentity, kind: "bpmn", namespace: "http://www.omg.org/spec/BPMN/20100524/MODEL", id: "Start_Application", type: "bpmn:startEvent", order: 2, parent: bpmnIdentity, attributes: { name: "Application received" }, source: sourceLocator },
      { identity: taskIdentity, kind: "bpmn", namespace: "http://www.omg.org/spec/BPMN/20100524/MODEL", id: "Task_Review", type: "bpmn:task", order: 3, parent: laneIdentity, attributes: { name: '<img src="https://example.invalid/x" onerror="window.pwned=1">' }, source: { artifact: "model.xml", element: "Task_Review" } },
      { identity: serviceIdentity, kind: "bpmn", namespace: "http://www.omg.org/spec/BPMN/20100524/MODEL", id: "Task_Service", type: "bpmn:task", order: 4, parent: bpmnIdentity, attributes: { name: "Assess eligibility" }, source: sourceLocator },
      { identity: subprocessIdentity, kind: "bpmn", namespace: "http://www.omg.org/spec/BPMN/20100524/MODEL", id: "SubProcess_Check", type: "bpmn:subProcess", order: 5, parent: bpmnIdentity, attributes: { name: "Eligibility check", isExpanded: "true" }, source: sourceLocator },
      { identity: nestedTaskIdentity, kind: "bpmn", namespace: "http://www.omg.org/spec/BPMN/20100524/MODEL", id: "Task_Nested", type: "bpmn:task", order: 6, parent: subprocessIdentity, attributes: { name: "Nested decision task" }, source: sourceLocator }
    ],
    relationships: [
      { identity: "rel-flow-start-review", kind: "sequenceFlow", source: startIdentity, target: taskIdentity, order: 0 },
      { identity: "rel-message-review-service", kind: "messageFlow", source: taskIdentity, target: serviceIdentity, order: 1 }
    ]
  };
  const dmnEvidence = {
    schema: "ce.models.v1",
    repositoryId: "repo-a",
    commitSha: revisionA,
    unitPath: "models/eligibility.dmn.json",
    sourceSha256: dmnHash,
    contentProfile: "model-content",
    coverage: { status: "full", supported: ["DMN decision tables"], limitations: [] },
    diagnostics: [],
    elements: [
      { identity: dmnIdentity, kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: "Decision_Eligibility", type: "dmn:decision", order: 0, attributes: { name: "Eligibility decision" }, source: { artifact: "eligibility.dmn", element: "Decision_Eligibility" } },
      { identity: tableIdentity, kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: "Table_Eligibility", type: "dmn:decisionTable", order: 1, parent: dmnIdentity, attributes: { name: "Eligibility", hitPolicy: "FIRST" }, hitPolicy: "FIRST", source: { artifact: "eligibility.dmn", element: "Table_Eligibility" } },
      { identity: `model:${"8".repeat(64)}`, kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: "InputClause_Age", type: "dmn:input", order: 2, parent: tableIdentity, attributes: { label: "Applicant age" }, source: { artifact: "eligibility.dmn", element: "InputClause_Age" } },
      { identity: `model:${"9".repeat(64)}`, kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: "Output_Eligibility", type: "dmn:output", order: 3, parent: tableIdentity, attributes: { label: "Eligibility" }, source: { artifact: "eligibility.dmn", element: "Output_Eligibility" } },
      ...["<18", "[18..24]", "-"].map((input, index) => ({
        identity: ruleIdentities[index], kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: `Rule_${index + 1}`,
        type: "dmn:rule", order: index + 4, parent: tableIdentity, attributes: {},
        cells: [{ kind: "input", order: 0, text: input }, { kind: "output", order: 1, text: [`"deny"`, `"manual"`, `"approve"`][index] }],
        source: { artifact: "eligibility.dmn", element: `Rule_${index + 1}` }
      })),
      { identity: `model:${"a".repeat(64)}`, kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: "Decision_Literal", type: "dmn:decision", order: 10, attributes: { name: "Threshold check" }, source: { artifact: "eligibility.dmn", element: "Decision_Literal" } },
      { identity: `model:${"b".repeat(64)}`, kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: "Literal_Threshold", type: "dmn:literalExpression", order: 11, parent: `model:${"a".repeat(64)}`, attributes: {}, content: { text: "score >= threshold", language: "FEEL" }, source: { artifact: "eligibility.dmn", element: "Literal_Threshold" } }
    ],
    relationships: []
  };
  const malformedDmnEvidence = structuredClone(dmnEvidence);
  malformedDmnEvidence.unitPath = "models/malformed-table.dmn.json";
  const firstMalformedRule = malformedDmnEvidence.elements.find((element) => element.id === "Rule_1") as { cells?: Array<{ kind: string; order: number; text: string }> };
  firstMalformedRule.cells?.pop();
  const candidateSource = {
    unitPath: "src/Eligibility.cs", commitSha: revisionA, sourceSha256: "f".repeat(64),
    start: 2, length: 10, startLine: 10, startColumn: 3, endLine: 10, endColumn: 13
  };
  const candidateEvidence = {
    schema: "ce.candidates.v1", recipe: "roslyn.scalar-guards.v1", repositoryId: "repo-a",
    commitSha: revisionA, unitPath: "src/Eligibility.cs", sourceSha256: "f".repeat(64),
    coverage: { status: "partial", scope: "single-file-intra-method", supportedMethods: 1, unsupportedMethods: 1 },
    diagnostics: [{ code: "UNSUPPORTED_METHOD", message: "One method was outside the bounded recipe." }],
    candidates: [{
      id: "IsEligible", kind: "decision", reviewStatus: "UNREVIEWED", executable: false, contentProfile: "candidate-content",
      source: candidateSource, parameters: [{ name: "age", symbol: "age", type: "int" }], inputsRead: ["age"], inputsWritten: [],
      body: { kind: "if", source: candidateSource, children: [
        { kind: "binary", source: candidateSource, operator: "LessThan", children: [
          { kind: "parameter", source: candidateSource, symbol: "age", type: "int", children: [] },
          { kind: "constant", source: candidateSource, type: "int", value: "18", children: [] }
        ] },
        { kind: "return", source: candidateSource, children: [{ kind: "constant", source: candidateSource, type: "bool", value: "false", children: [] }] }
      ] }
    }]
  };
  const oversizedEvidence = { ...bpmnEvidence, commitSha: revisionB, unitPath: "models/oversized.bpmn.json" };
  const deniedEvidence = { ...bpmnEvidence, unitPath: "models/denied.bpmn.json" };
  const malformedEvidence = { ...bpmnEvidence, unitPath: "models/malformed.bpmn.json" };
  const tamperedEvidence = { ...bpmnEvidence, unitPath: "models/tampered.bpmn.json" };
  const items: SummaryFixture[] = [
    { id: "model-bpmn", kind: "authored", repositoryId: "repo-a", commitSha: revisionA, unitPath: "models/application.bpmn.json", modelKind: "bpmn", coverage: "partial", diagnosticCount: 1, source: { status: "available", sha256: bpmnHash } },
    { id: "model-dmn", kind: "authored", repositoryId: "repo-a", commitSha: revisionA, unitPath: "models/eligibility.dmn.json", modelKind: "dmn", coverage: "full", diagnosticCount: 0, source: { status: "available", sha256: dmnHash } },
    { id: "candidate-cs", kind: "candidate", repositoryId: "repo-a", commitSha: revisionA, unitPath: "src/Eligibility.cs", modelKind: "candidate", coverage: "partial", diagnosticCount: 1, source: { status: "missing", sha256: "f".repeat(64) } },
    { id: "model-denied", kind: "authored", repositoryId: "repo-a", commitSha: revisionA, unitPath: "models/denied.bpmn.json", modelKind: "bpmn", coverage: "full", diagnosticCount: 0, source: { status: "denied", sha256: bpmnHash } },
    { id: "oversize-bpmn", kind: "authored", repositoryId: "repo-a", commitSha: revisionB, unitPath: "models/oversized.bpmn.json", modelKind: "bpmn", coverage: "partial", diagnosticCount: 1, source: { status: "available", sha256: bpmnHash } },
    { id: "model-malformed", kind: "authored", repositoryId: "repo-a", commitSha: revisionA, unitPath: "models/malformed.bpmn.json", modelKind: "bpmn", coverage: "full", diagnosticCount: 0, source: { status: "available", sha256: bpmnHash } },
    { id: "model-tampered", kind: "authored", repositoryId: "repo-a", commitSha: revisionA, unitPath: "models/tampered.bpmn.json", modelKind: "bpmn", coverage: "full", diagnosticCount: 0, source: { status: "available", sha256: bpmnHash } },
    { id: "model-missing", kind: "authored", repositoryId: "repo-a", commitSha: revisionA, unitPath: "models/missing.bpmn.json", modelKind: "bpmn", coverage: "full", diagnosticCount: 0, source: { status: "missing", sha256: bpmnHash } },
    { id: "model-no-di", kind: "authored", repositoryId: "repo-a", commitSha: revisionA, unitPath: "models/no-di.bpmn.json", modelKind: "bpmn", coverage: "partial", diagnosticCount: 0, source: { status: "available", sha256: noDiHash } },
    { id: "model-dmn-malformed", kind: "authored", repositoryId: "repo-a", commitSha: revisionA, unitPath: "models/malformed-table.dmn.json", modelKind: "dmn", coverage: "partial", diagnosticCount: 0, source: { status: "available", sha256: dmnHash } }
  ];
  const missingEvidence = { ...bpmnEvidence, unitPath: "models/missing.bpmn.json" };
  const noDiEvidence = { ...bpmnEvidence, unitPath: "models/no-di.bpmn.json", sourceSha256: noDiHash };
  const evidenceById: Record<string, { unitPath: string; sourceSha256: string }> = {
    "model-bpmn": bpmnEvidence, "model-dmn": dmnEvidence, "model-dmn-malformed": malformedDmnEvidence, "candidate-cs": candidateEvidence,
    "model-denied": deniedEvidence, "oversize-bpmn": oversizedEvidence,
    "model-malformed": malformedEvidence, "model-tampered": tamperedEvidence,
    "model-missing": missingEvidence, "model-no-di": noDiEvidence
  };
  const xmlById: Record<string, string> = {
    "model-bpmn": bpmnXml, "model-dmn": dmnXml, "model-dmn-malformed": dmnXml, "model-denied": bpmnXml,
    "model-tampered": bpmnXml.replace("Application received", "Tampered source"),
    "model-no-di": bpmnXml.replace(/<bpmndi:BPMNDiagram[\s\S]*?<\/bpmndi:BPMNDiagram>/, "")
  };
  return { items, evidenceById, xmlById };
}
