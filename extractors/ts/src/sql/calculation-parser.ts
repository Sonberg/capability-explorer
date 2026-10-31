import type { Catalog, RoutineDef } from "./ddl.ts";
import type { SqlCalculationParseOptions, SqlCalculationParseResult } from "./calculation-types.ts";

export const SQL_CALCULATION_MAX_TOKENS = 4096;
export const SQL_CALCULATION_MAX_DEPTH = 64;

/** Parse the bounded static calculation subset without executing SQL. */
export function parseRoutineCalculation(
  _catalog: Catalog,
  routine: RoutineDef,
  _options: SqlCalculationParseOptions = {},
): SqlCalculationParseResult {
  if (routine.kind !== "Routine" || !routine.routineKind?.includes("function")) return { status: "not-candidate" };
  return { status: "unsupported", reason: "calculation parser implementation pending", at: routine.at };
}
