import {buildLiveModel} from './live-model.ts';
import type {LiveModelRequest,LiveModelResponse} from './live-contracts.ts';

// The owner terminates this worker when scope changes; obsolete work cannot publish.
const runtime=self as unknown as {
  onmessage:((event:MessageEvent<LiveModelRequest>)=>void)|null;
  postMessage:(response:LiveModelResponse)=>void;
};
runtime.onmessage=({data})=>{
  try {runtime.postMessage({requestId:data.requestId,ok:true,result:buildLiveModel(data)});}
  catch(error){runtime.postMessage({requestId:data.requestId,ok:false,error:error instanceof Error?error.message:'Could not build architecture model'});}
};
