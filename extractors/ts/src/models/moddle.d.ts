declare module "bpmn-moddle" {
  export class BpmnModdle {
    fromXML(xml: string, options?: { lax?: boolean }): Promise<{ rootElement: unknown; warnings: Error[] }>;
  }
}

declare module "dmn-moddle" {
  export class DmnModdle {
    fromXML(xml: string, options?: { lax?: boolean }): Promise<{ rootElement: unknown; warnings: Error[] }>;
  }
}
