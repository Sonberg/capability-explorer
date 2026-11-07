import type {LayoutedLikeC4ModelData} from '@likec4/core/types';
import type {Group} from './types.ts';

export const LIVE_MODEL_VERSION='live-likec4-v1';
/** Graph category of a selected-catalogue HTTP target group with no known provider (#102). */
export const UNREGISTERED_HTTP_TARGET_CATEGORY='unregistered-http-target';
/** Visible node badge; candidates are observed targets, not Capabilities or proven services. */
export const UNREGISTERED_HTTP_TARGET_BADGE='Unregistered target';

/** Structural subset of Architecture.Graph; extra evidence fields are retained verbatim. */
export interface LiveNode {
  readonly id:string;
  readonly label?:string|null;
  readonly category?:string|null;
  readonly repositoryId?:string|null;
  readonly symbolKey?:string|null;
  readonly sublabel?:string|null;
  readonly group?:string|null;
  readonly badges?:readonly string[]|null;
  readonly drill?:{readonly level:string;readonly focus:string}|null;
}
export interface LiveEdge {
  readonly id:string;
  readonly from:string;
  readonly to:string;
  readonly kind?:string;
  readonly label?:string;
  readonly certainty?:string;
  readonly count?:number;
  readonly facts?:readonly unknown[];
}
export interface LiveGraph {
  readonly title?:string;
  readonly level?:string;
  readonly focus?:string;
  readonly breadcrumbs?:readonly {readonly label:string;readonly level:string;readonly focus?:string}[];
  readonly notes?:readonly string[];
  readonly nodes:readonly LiveNode[];
  readonly edges:readonly LiveEdge[];
}
export interface LiveModelInput {
  readonly graph:LiveGraph;
  readonly snapshot:string;
  readonly associationIds:readonly string[];
}
export interface LiveModelResult {
  readonly snapshot:string;
  readonly source:string;
  readonly modelData:LayoutedLikeC4ModelData;
  /** Rendered node -> original IDs (containers include their represented descendants). */
  readonly nodeSourceIds:Readonly<Record<string,readonly string[]>>;
  /** Rendered edge -> every original contributing edge, including collapsed relationships. */
  readonly edgeSourceIds:Readonly<Record<string,readonly string[]>>;
  readonly sourceNodeIds:Readonly<Record<string,string>>;
  readonly groups:Group[];
}
export type LiveModelRequest=LiveModelInput & {readonly requestId:number};
export type LiveModelResponse={readonly requestId:number;readonly ok:true;readonly result:LiveModelResult}
  |{readonly requestId:number;readonly ok:false;readonly error:string};
