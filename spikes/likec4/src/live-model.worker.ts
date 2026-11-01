import {buildLiveModel} from './live-model.ts';
import type {LiveModelRequest,LiveModelResponse} from './live-contracts.ts';
import {loadGraphvizLayout} from './live-model-graphviz.ts';

// The owner terminates this worker when scope changes; obsolete work cannot publish.
const runtime=self as unknown as {
  onmessage:((event:MessageEvent<LiveModelRequest>)=>void)|null;
  postMessage:(response:LiveModelResponse)=>void;
};
let layout:ReturnType<typeof loadGraphvizLayout>|undefined;
runtime.onmessage=async({data})=>{
  try {layout??=loadGraphvizLayout();runtime.postMessage({requestId:data.requestId,ok:true,result:buildLiveModel(data,await layout)});}
  catch(error){runtime.postMessage({requestId:data.requestId,ok:false,error:error instanceof Error?error.message:'Could not build architecture model'});}
};
