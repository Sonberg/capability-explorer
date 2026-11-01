import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createSearch} from '../src/routing-search.ts';
import {searchBaseline} from './search-baseline.ts';
test('pooled typed search preserves exact baseline paths across changing grids and heading ties',()=>{
 let seed=1729;const next=()=>{seed=(Math.imul(seed,1664525)+1013904223)>>>0;return seed;};
 const search=createSearch();
 for(let run=0;run<150;run++){
  const width=8+next()%12,height=8+next()%12,size=width*height;
  const grid={width,height,left:-24,top:12,blocked:new Uint8Array(size),headers:new Uint8Array(size),horizontal:new Uint16Array(size),vertical:new Uint16Array(size),terminalClearance:new Uint16Array(size)};
  for(let i=0;i<size;i++){grid.blocked[i]=next()%13===0?1:0;grid.headers[i]=next()%19===0?1:0;grid.horizontal[i]=next()%23===0?1:0;grid.vertical[i]=next()%29===0?1:0;grid.terminalClearance[i]=next()%31===0?1:0;}
  const start={point:[-12,24],stub:[-12,24],side:run%4},end={point:[(width-2)*12-24,(height-2)*12+12],stub:[(width-2)*12-24,(height-2)*12+12],side:(run>>2)%4};
  assert.deepEqual(search(grid,start,end),searchBaseline(grid,start,end),`query ${run}`);
 }
});
