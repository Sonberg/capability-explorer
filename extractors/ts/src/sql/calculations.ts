import { ArtifactBuilder } from "../extract/builder.ts";
import { parseRoutineCalculation } from "./calculation-parser.ts";
import { calculationAttributes } from "./calculation-attributes.ts";
import { CalculationLowering, numeric, scalarType, UnsupportedCalculation } from "./calculation-ir.ts";
import type { SqlRoutineCalculation } from "./calculation-types.ts";
import type { Catalog, Range, RoutineDef } from "./ddl.ts";

/** Lower a fully parsed routine before the builder sees any calculation claim. */
export function lowerRoutineCalculation(catalog: Catalog, calculation: SqlRoutineCalculation, key: (object: string) => string) {
  const lowering = new CalculationLowering(catalog, calculation, key);
  const value = lowering.lower(calculation.expression);
  if (calculation.query && !lowering.aggregates.length) throw new UnsupportedCalculation("table row multiplicity requires an aggregate");
  if (!numeric(value.type)) throw new UnsupportedCalculation("calculation output is not numeric");
  const output = calculation.outputType;
  if (catalog.dialect === "tsql" && ["decimal", "numeric"].includes(output.name.toLowerCase())) throw new UnsupportedCalculation("T-SQL decimal return precision and scale cannot be represented faithfully");
  if (output.precision !== undefined || output.scale !== undefined) throw new UnsupportedCalculation("constrained return precision and scale cannot be represented faithfully");
  if (output.name.toLowerCase() !== "table" && scalarType(output.name, catalog.dialect) !== value.type) throw new UnsupportedCalculation("implicit return conversion is unsupported");
  return { lowering, expr: value.ir, inputs: lowering.inputs, output: { type: value.type, unit: { status: "unknown" } } };
}

/** Emit conservative static calculation facts; unsupported semantics never change graph coverage. */
export function emitRoutineCalculation(
  catalog: Catalog,
  routine: RoutineDef,
  builder: ArtifactBuilder,
  key: (object: string, column?: string) => string,
  ev: (range: Range) => string,
): void {
  const parsed = parseRoutineCalculation(catalog, routine);
  if (parsed.status === "not-candidate") return;
  const unsupported = (reason: string, at: { start: number; end: number }) => builder.diagnostic(
    "info", "SQL_CALCULATION_UNSUPPORTED", `${routine.name}: ${reason}`, routine.path, false, ev({ path: routine.path, ...at }),
  );
  if (parsed.status === "unsupported") { unsupported(parsed.reason, parsed.at); return; }
  try {
    const lowered = lowerRoutineCalculation(catalog, parsed.calculation, key);
    const attributes = calculationAttributes(parsed.calculation, lowered.lowering, routine.path, ev);
    builder.calculation({
      symbolKey: key(routine.name), output: lowered.output, inputs: lowered.inputs, expr: lowered.expr,
      attributes, evidence: [ev(routine.at)],
    });
  } catch (error) {
    if (!(error instanceof UnsupportedCalculation)) throw error;
    unsupported(error.message, parsed.calculation.expression.at);
  }
}
