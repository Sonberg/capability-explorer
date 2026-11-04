import { parentPort, workerData } from "node:worker_threads";
import { extractModelXmlInProcess } from "./extract.ts";
import { ModelXmlError } from "./xml.ts";
import type { ModelEvidence, ModelExtractionOptions } from "./profile.ts";

interface ModelWorkerInput {
  xml: string;
  options: ModelExtractionOptions;
  sourceBytes: Uint8Array;
}

if (!parentPort) throw new Error("Model extraction worker requires a parent port");

const input = workerData as ModelWorkerInput;
try {
  const evidence: ModelEvidence = await extractModelXmlInProcess(input.xml, input.options, input.sourceBytes);
  parentPort.postMessage({ ok: true, evidence });
} catch (error) {
  parentPort.postMessage({
    ok: false,
    code: error instanceof ModelXmlError ? error.code : "model-extraction-failed",
    message: error instanceof ModelXmlError ? error.message : "Model extraction failed",
  });
} finally {
  parentPort.close();
}
