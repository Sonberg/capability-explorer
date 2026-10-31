const MAX_DISCOUNT = -0.2;
const MAX_SURCHARGE = 0.3;
const SENSITIVITY = 0.5;

export function priceForDemand(baseOre: number, ratio: number): number {
  const adjustment = Math.min(Math.max(ratio - 1, MAX_DISCOUNT), MAX_SURCHARGE) * SENSITIVITY;
  return Math.trunc(baseOre * (1 + adjustment));
}
