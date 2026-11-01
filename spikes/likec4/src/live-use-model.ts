import {useEffect, useMemo, useRef, useState} from 'react';
import type {LiveGraph, LiveModelRequest, LiveModelResponse, LiveModelResult} from './live-contracts.ts';
import {LIVE_MODEL_VERSION} from './live-contracts.ts';

// Both entry count and retained bytes are bounded; snapshot IDs alone are never cache keys.
const cache = new Map<string, {result:LiveModelResult; bytes:number}>();
const MAX_BYTES = 12 * 1024 * 1024;
let retainedBytes = 0;
function remember(key:string, result:LiveModelResult) {
  const bytes = 2 * (key.length + JSON.stringify(result).length);
  if (bytes > MAX_BYTES) return;
  const previous = cache.get(key);
  if (previous) retainedBytes -= previous.bytes;
  cache.delete(key);
  cache.set(key, {result, bytes}); retainedBytes += bytes;
  while (cache.size > 3 || retainedBytes > MAX_BYTES) {
    const oldest = cache.keys().next().value;
    if (oldest === undefined) break;
    retainedBytes -= cache.get(oldest)!.bytes; cache.delete(oldest);
  }
}
type State = {key:string; generation:number; result?:LiveModelResult; error?:string};

export function useLiveModel(graph:LiveGraph, snapshot:string, associationIds?:ReadonlySet<string>) {
  const associationKey = JSON.stringify([...(associationIds ?? [])].sort());
  const input = useMemo(() => ({graph, snapshot, associationIds:JSON.parse(associationKey) as string[]}), [graph, snapshot, associationKey]);
  const key = useMemo(() => JSON.stringify({version:LIVE_MODEL_VERSION, ...input}), [input]);
  const generation = useRef(0);
  const [retry, setRetry] = useState(0);
  const [state, setState] = useState<State>({key:'', generation:0});
  useEffect(() => {
    const requestId = ++generation.current;
    const cached = cache.get(key);
    if (cached) {
      cache.delete(key); cache.set(key, cached);
      setState({key, generation:requestId, result:cached.result});
      return;
    }
    setState({key, generation:requestId});
    let active = true;
    let worker:Worker | undefined;
    let deadline:ReturnType<typeof setTimeout> | undefined;
    const stop = () => {clearTimeout(deadline); worker?.terminate();};
    const fail = (error:string) => {stop(); if (active) setState({key, generation:requestId, error});};
    try {
      worker = new Worker(new URL('./live-model.worker.ts', import.meta.url), {type:'module'});
      worker.onmessage = ({data}:MessageEvent<LiveModelResponse>) => {
        if (!active || data.requestId !== requestId) return;
        stop();
        if (!data.ok) {fail(data.error); return;}
        if (data.result.snapshot !== snapshot) {fail('The background model returned a different snapshot.'); return;}
        remember(key, data.result);
        setState({key, generation:requestId, result:data.result});
      };
      worker.onerror = event => {event.preventDefault(); fail('Background diagram compilation failed. Retry to rebuild this snapshot.');};
      worker.onmessageerror = () => fail('The background diagram result could not be read.');
      deadline = setTimeout(() => fail('Diagram compilation exceeded 30 seconds. Retry or select a smaller scope.'), 30_000);
      worker.postMessage({...input, requestId} satisfies LiveModelRequest);
    } catch (error) {fail(error instanceof Error ? error.message : String(error));}
    return () => {active = false; stop();};
  }, [input, key, retry, snapshot]);
  // A changed input hides old data in the render before effect cleanup runs.
  const current = state.key === key ? state : {key, generation:generation.current + 1};
  return {...current, identity:key, retry:() => setRetry(value => value + 1)};
}
