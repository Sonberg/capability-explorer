// Presentation geometry is structural so fixture tests need no renderer internals.
export type Point = readonly [number, number];
export type MutablePoint = [number, number];
export interface Box { x:number; y:number; width:number; height:number }
export type {DiagramNode,DiagramEdge,LayoutedView as DiagramView} from 'likec4/model';
export interface SourceNode {
  id:string; label?:string; category?:string; repositoryId?:string; symbolKey?:string; sublabel?:string;
}
export interface SourceFact {
  relation:string;
  to?:{key?:string;declared?:boolean;kind?:string};
}
export interface SourceEdge {id:string;from:string;to:string;facts?:SourceFact[]}
export interface SourceGraph {nodes:SourceNode[];edges:SourceEdge[]}
export interface Group {id:string;title:string;members:string[];origin:'repository'|'user';owner?:string}
export type RoutingResult<T> = {ok:true;view:T;milliseconds:number}|{ok:false;error:string};
export type RoutingRequest<T> = {view:T};
