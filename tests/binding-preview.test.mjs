import test from 'node:test';
import assert from 'node:assert/strict';
import { editPreview, reviewPreview, previewEntries, canExportPreview } from '../platform/src/CapabilityExplorer.Explorer/client/src/views/BindingPreviewState.ts';
test('editing destination or note invalidates review without changing original draft',()=>{
  const original={h:{hint:'h',target:'one',note:'reviewed',reviewed:true}};
  for(const field of ['target','note']) {const next=editPreview(original,'h',field,'changed');assert.equal(next.h.reviewed,false);assert.equal(original.h.reviewed,true);}
});
test('review only applies to an explicitly entered mapping',()=>{assert.deepEqual(reviewPreview({},'h',true),{});const draft=editPreview({},'h','target','service');assert.equal(reviewPreview(draft,'h',true).h.reviewed,true);});
test('blank drafts are omitted and explicit mappings are sorted deterministically',()=>{const draft={z:{hint:'z',target:'z',note:'',reviewed:false},a:{hint:'a',target:'a',note:'',reviewed:false},empty:{hint:'empty',target:'  ',note:'',reviewed:true}};assert.deepEqual(previewEntries(draft).map(e=>e.hint),['a','z']);});
test('stale preview, no rows, uncertain/conflicting or unreviewed rows cannot enable export',()=>{
  const valid={fingerprint:'current',canExport:true,rows:[{hint:'h',target:'service',state:'evidenced',message:'',reviewed:true,edgeCount:1}]};
  assert.equal(canExportPreview(valid,'current'),true);assert.equal(canExportPreview(valid,'stale'),false);assert.equal(canExportPreview({...valid,rows:[]},'current'),false);
  for(const state of ['uncertain','conflicting']) assert.equal(canExportPreview({...valid,rows:[{...valid.rows[0],state}]},'current'),false);
  assert.equal(canExportPreview({...valid,rows:[{...valid.rows[0],reviewed:false}]},'current'),false);
});
