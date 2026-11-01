import type { Dialect } from "./lexer.ts";

/** Absolute, end-exclusive offsets in the routine's source file. */
export interface SqlSpan { start: number; end: number }

/** A declared SQL type. Names retain dialect identifier normalization. */
export interface SqlDataType {
  name: string;
  precision?: number;
  scale?: number;
  at: SqlSpan;
}

export interface SqlParameter {
  /** Declared name, including @ for T-SQL; unquoted PostgreSQL names are folded. */
  name: string;
  type: SqlDataType;
  at: SqlSpan;
}

export type SqlExpression =
  | { kind: "number"; value: string; at: SqlSpan }
  | { kind: "string"; value: string; at: SqlSpan }
  | { kind: "null"; at: SqlSpan }
  | { kind: "reference"; parts: string[]; at: SqlSpan }
  | { kind: "unary"; operator: "+" | "-"; operand: SqlExpression; at: SqlSpan }
  | { kind: "binary"; operator: "+" | "-" | "*" | "/" | "=" | "<>" | "!=" | "<" | "<=" | ">" | ">=" | "AND"; left: SqlExpression; right: SqlExpression; at: SqlSpan }
  | { kind: "between"; expression: SqlExpression; lower: SqlExpression; upper: SqlExpression; at: SqlSpan }
  | { kind: "call"; name: string; args: SqlExpression[]; distinct: boolean; at: SqlSpan }
  | { kind: "cast"; expression: SqlExpression; type: SqlDataType; at: SqlSpan };

export interface SqlCalculationQuery {
  /** Schema-qualified source table, using existing DDL name rules. */
  table: string;
  alias?: string;
  where?: SqlExpression;
  at: SqlSpan;
}

export interface SqlRoutineCalculation {
  dialect: Dialect;
  expression: SqlExpression;
  query?: SqlCalculationQuery;
  parameters: SqlParameter[];
  /** `table` for an inline table-valued return; lowering infers its scalar expression type. */
  outputType: SqlDataType;
  at: SqlSpan;
}

export type SqlCalculationParseResult =
  | { status: "supported"; calculation: SqlRoutineCalculation }
  | { status: "unsupported"; reason: string; at: SqlSpan }
  | { status: "not-candidate" };

export interface SqlCalculationParseOptions { maxTokens?: number; maxDepth?: number }
