import { Parser, type SaxElement } from "saxen";

export const BPMN_NAMESPACE = "http://www.omg.org/spec/BPMN/20100524/MODEL";
export const DMN_13_NAMESPACE = "https://www.omg.org/spec/DMN/20191111/MODEL/";

export interface XmlNode {
  qualifiedName: string;
  localName: string;
  namespace: string;
  attributes: Record<string, string>;
  text: string;
  children: XmlNode[];
  parent?: XmlNode;
}

export interface XmlLimits {
  maxBytes: number;
  maxDepth: number;
  maxElements: number;
  maxAttributesPerElement: number;
  maxTextBytes: number;
  maxAttributeBytes: number;
  maxParseMilliseconds: number;
}

export const DEFAULT_XML_LIMITS: XmlLimits = {
  maxBytes: 4 * 1024 * 1024,
  maxDepth: 128,
  maxElements: 25_000,
  maxAttributesPerElement: 128,
  maxTextBytes: 2 * 1024 * 1024,
  maxAttributeBytes: 2 * 1024 * 1024,
  maxParseMilliseconds: 5_000,
};

export class ModelXmlError extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = "ModelXmlError";
    this.code = code;
  }
}

const unsafeDoctypeOrEntity = /<!\s*(?:DOCTYPE|ENTITY)\b/i;
const unsafeEntityReference = /&(?!(?:amp|apos|gt|lt|quot|#\d+|#x[\da-f]+);)[A-Za-z_][\w.:-]*;/i;
const standardEntities = /&(?:amp|apos|gt|lt|quot|#\d+|#x[\da-f]+);/gi;

export function parseXml(xml: string, limits = DEFAULT_XML_LIMITS): XmlNode {
  const start = performance.now();
  const bytes = Buffer.byteLength(xml, "utf8");
  if (bytes > limits.maxBytes) throw new ModelXmlError("xml-byte-limit", `XML byte limit exceeded (${limits.maxBytes})`);
  if (unsafeDoctypeOrEntity.test(xml)) throw new ModelXmlError("xml-doctype-disabled", "DTD and entity declarations are disabled");
  const withoutStandardEntities = xml.replace(standardEntities, "");
  if (unsafeEntityReference.test(withoutStandardEntities)) {
    throw new ModelXmlError("xml-entity-disabled", "Non-standard entity references are disabled");
  }

  const parser = new Parser({ proxy: true });
  parser.ns();
  const stack: XmlNode[] = [];
  let root: XmlNode | undefined;
  let elementCount = 0;
  let textBytes = 0;
  let attributeBytes = 0;
  let parseFailure: Error | undefined;

  parser.on("openTag", (element: SaxElement, decode, selfClosing) => {
    elementCount++;
    if (elementCount > limits.maxElements) throw new ModelXmlError("xml-element-limit", `XML element limit exceeded (${limits.maxElements})`);
    if (stack.length + 1 > limits.maxDepth) throw new ModelXmlError("xml-depth-limit", `XML nesting limit exceeded (${limits.maxDepth})`);
    const entries = Object.entries(element.attrs).map(([key, value]) => [key, decode(value)] as const);
    if (entries.length > limits.maxAttributesPerElement) {
      throw new ModelXmlError("xml-attribute-limit", `XML attribute count limit exceeded (${limits.maxAttributesPerElement})`);
    }
    attributeBytes += entries.reduce((sum, [key, value]) => sum + Buffer.byteLength(key) + Buffer.byteLength(value), 0);
    if (attributeBytes > limits.maxAttributeBytes) {
      throw new ModelXmlError("xml-attribute-byte-limit", `XML attribute byte limit exceeded (${limits.maxAttributeBytes})`);
    }

    const [prefix, ...localParts] = element.name.split(":");
    const localName = localParts.length ? localParts.join(":") : prefix!;
    const namespace = localParts.length
      ? element.ns[`${prefix}$uri`] ?? ""
      : element.ns["xmlns$uri"] ?? "";
    const node: XmlNode = {
      qualifiedName: element.originalName,
      localName,
      namespace,
      attributes: Object.fromEntries(entries.sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)),
      text: "",
      children: [],
    };
    const parent = stack.at(-1);
    if (parent) {
      node.parent = parent;
      parent.children.push(node);
    } else if (root) {
      throw new ModelXmlError("xml-multiple-roots", "XML document has more than one root element");
    } else {
      root = node;
    }
    if (!selfClosing) stack.push(node);
  });

  parser.on("closeTag", (element: SaxElement, _decode, selfClosing) => {
    if (selfClosing) return;
    const closed = stack.pop();
    if (!closed || closed.qualifiedName !== element.originalName) {
      throw new ModelXmlError("xml-mismatched-tags", "XML elements are not properly nested");
    }
  });

  const appendText = (value: string) => {
    textBytes += Buffer.byteLength(value);
    if (textBytes > limits.maxTextBytes) throw new ModelXmlError("xml-text-limit", `XML text limit exceeded (${limits.maxTextBytes})`);
    const current = stack.at(-1);
    if (current) current.text += value;
    else if (value.trim()) throw new ModelXmlError("xml-text-outside-root", "Non-whitespace text outside the root element");
  };
  parser.on("text", (value, decode) => appendText(decode(value)));
  parser.on("cdata", appendText);
  parser.on("error", (error) => { parseFailure = error; });
  parser.on("warn", (error) => { parseFailure ??= error; });
  const returnedError = parser.parse(xml);
  if (returnedError || parseFailure) throw new ModelXmlError("xml-malformed", "XML parser rejected malformed or unsupported syntax");
  if (stack.length || !root) throw new ModelXmlError("xml-malformed", "XML document is incomplete or empty");
  if (performance.now() - start > limits.maxParseMilliseconds) {
    throw new ModelXmlError("xml-time-limit", `XML parse budget exceeded (${limits.maxParseMilliseconds}ms)`);
  }
  return root;
}
