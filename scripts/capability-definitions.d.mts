export interface CapabilityDefinition {
  id: string;
  repositories?: unknown[];
}

export interface CapabilityDefinitions {
  capabilities: CapabilityDefinition[];
}

export function loadCapabilityDefinitions(path: string): CapabilityDefinitions;