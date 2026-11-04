declare module "saxen" {
  export interface SaxElement {
    originalName: string;
    name: string;
    attrs: Record<string, string>;
    ns: Record<string, string>;
  }

  export type DecodeEntities = (value: string) => string;

  export class Parser {
    constructor(options?: { proxy?: boolean });
    ns(prefixes?: Record<string, string>): void;
    on(event: "openTag", handler: (element: SaxElement, decode: DecodeEntities, selfClosing: boolean, context: () => unknown) => void): this;
    on(event: "closeTag", handler: (element: SaxElement, decode: DecodeEntities, selfClosing: boolean, context: () => unknown) => void): this;
    on(event: "text" | "cdata", handler: (value: string, decode: DecodeEntities, context: () => unknown) => void): this;
    on(event: "error" | "warn", handler: (error: Error, context: () => unknown) => void): this;
    parse(xml: string): Error | null;
  }
}
