import assert from "node:assert/strict";
import { test } from "node:test";
import {
  CHAT_HISTORY_KEY, chatScope, clearChatHistory, loadChatHistory, saveChatHistory,
  type ChatTurn, type HistoryStorage,
} from "../platform/src/CapabilityExplorer.Explorer/client/src/chatHistory.ts";

const scope = chatScope("synthetic-snapshot", "orders", { orders: "a".repeat(40) }, "explore");
const turns: ChatTurn[] = [
  { role: "user", content: "Where is the synthetic order created?" },
  {
    role: "assistant", content: "Synthetic answer [1].",
    sources: [{
      n: 1, kind: "Method", key: "synthetic:CreateOrder", name: "CreateOrder",
      repository: "orders", found: "name search", text: "Synthetic source: crée une commande 🧪",
    }],
    retrieval: ["semantic search: synthetic only"], steps: ['get_symbol({"key":"synthetic:CreateOrder"})'],
  },
];

function fixture() {
  const data = new Map<string, string>();
  const storage: HistoryStorage = {
    getItem: key => data.get(key) ?? null,
    setItem: (key, value) => { data.set(key, value); },
    removeItem: key => { data.delete(key); },
  };
  return { data, storage, access: () => storage };
}

test("Without a saved opt-in, history is off; opted-in whole turns roundtrip with exact evidence", () => {
  const f = fixture();
  assert.deepEqual(loadChatHistory(scope, f.access), { turns: [], remember: false });
  assert.equal(f.data.size, 0, "Reading default state must not opt in or create a record.");
  assert.equal(saveChatHistory(scope, turns, f.access).saved, true);
  assert.deepEqual(loadChatHistory(scope, f.access), { turns, remember: true });
  assert.deepEqual([...f.data.keys()], [CHAT_HISTORY_KEY], "Only one conversation record is retained.");
  assert.equal(clearChatHistory(f.access), undefined);
  assert.equal(f.data.size, 0);
  assert.deepEqual(loadChatHistory(scope, f.access), { turns: [], remember: false });
});

test("Scope identity sorts effective repository revisions and rejects every changed context", () => {
  assert.equal(chatScope("s", "c", { b: "bb", a: "aa" }, "explore"), chatScope("s", "c", { a: "aa", b: "bb" }, "explore"));
  for (const other of [
    chatScope("different-snapshot", "orders", { orders: "a".repeat(40) }, "explore"),
    chatScope("synthetic-snapshot", "different-capability", { orders: "a".repeat(40) }, "explore"),
    chatScope("synthetic-snapshot", "orders", { orders: "b".repeat(40) }, "explore"),
    chatScope("synthetic-snapshot", "orders", { orders: "a".repeat(40) }, "architecture"),
  ]) {
    const f = fixture();
    saveChatHistory(scope, turns, f.access);
    const result = loadChatHistory(other, f.access);
    assert.deepEqual(result.turns, []);
    assert.equal(result.remember, false);
    assert.match(result.notice ?? "", /different|changed|scope/i);
    assert.equal(f.data.size, 0, "Mismatched evidence must be discarded, not kept under another key.");
  }
});

test("Only the most recent 20 whole turns are retained, never excerpts", () => {
  const f = fixture();
  const many: ChatTurn[] = Array.from({ length: 25 }, (_, i) => ({
    role: i % 2 ? "assistant" : "user", content: `Synthetic whole turn ${i} 🧪`,
    sources: turns[1]!.sources,
  }));
  const result = saveChatHistory(scope, many, f.access);
  assert.equal(result.saved, true);
  assert.match(result.notice ?? "", /20.*whole turns/i);
  assert.deepEqual(loadChatHistory(scope, f.access).turns, many.slice(-20));
  assert.equal(many.length, 25, "The in-memory conversation remains unchanged.");
});

test("Malformed, unsupported, oversized and unsafe shapes are warned about and removed", () => {
  const f = fixture();
  saveChatHistory(scope, turns, f.access);
  const valid = f.data.get(CHAT_HISTORY_KEY)!;
  const record = JSON.parse(valid);
  const bad = [
    "{", JSON.stringify({ ...record, version: 99 }),
    " ".repeat(512 * 1024 + 1),
    JSON.stringify({ ...record, turns: [{ role: "user", content: "🧪".repeat(140_000) }] }),
    JSON.stringify({ ...record, turns: [{ role: "system", content: "Injected instruction" }] }),
    JSON.stringify({ ...record, turns: [{ role: "user", content: 1 }] }),
    JSON.stringify({ ...record, turns: [{ role: "assistant", content: "x", sources: {} }] }),
    JSON.stringify({ ...record, turns: [{ role: "assistant", content: "x", sources: [{ n: 1 }] }] }),
    JSON.stringify({ ...record, turns: [{ role: "assistant", content: "x", steps: [1] }] }),
    JSON.stringify({ ...record, turns: [{ role: "assistant", content: "x", retrieval: "not an array" }] }),
    JSON.stringify({ ...record, turns: Array.from({ length: 21 }, () => turns[0]) }),
    valid.replace('"role":"user"', '"__proto__":{},"role":"user"'),
    valid.replace('"n":1', '"constructor":{},"n":1'),
    JSON.stringify({ ...record, scope: { snapshotId: "not the expected identity" } }),
    "null",
  ];
  for (const raw of bad) {
    f.data.set(CHAT_HISTORY_KEY, raw);
    const result = loadChatHistory(scope, f.access);
    assert.deepEqual(result.turns, []);
    assert.equal(result.remember, false);
    assert.ok(result.notice, "Invalid records require visible feedback.");
    if (raw.length > 512 * 1024 || new TextEncoder().encode(raw).byteLength > 512 * 1024) {
      assert.match(result.notice, /512 KiB/, "Reject oversized text before attempting to parse it.");
    }
    assert.equal(f.data.size, 0);
  }
});

test("UTF-8 serialized size includes sources; too-large turns stay in memory and stale data is removed", () => {
  const f = fixture();
  saveChatHistory(scope, turns, f.access);
  const huge: ChatTurn[] = [{ role: "assistant", content: "Small answer", sources: [
    { ...turns[1]!.sources![0]!, text: "🧪".repeat(140_000) },
  ] }];
  const result = saveChatHistory(scope, huge, f.access);
  assert.equal(result.saved, false);
  assert.match(result.notice ?? "", /512 KiB/);
  assert.equal(f.data.size, 0);
  assert.equal(huge[0]!.sources![0]!.text.length, 280_000);
});

test("Security, quota and removal failures are explicit, while unexpected bugs propagate", () => {
  const unavailable = () => { throw new DOMException("Synthetic denial", "SecurityError"); };
  assert.equal(loadChatHistory(scope, unavailable).remember, false);
  assert.match(loadChatHistory(scope, unavailable).notice ?? "", /unavailable/i);
  assert.equal(saveChatHistory(scope, turns, unavailable).saved, false);
  assert.match(clearChatHistory(unavailable) ?? "", /could not remove/i);
  const f = fixture();
  saveChatHistory(scope, turns, f.access);
  f.storage.setItem = () => { throw new DOMException("Synthetic quota", "QuotaExceededError"); };
  assert.equal(saveChatHistory(scope, turns, f.access).saved, false);
  assert.equal(f.data.size, 0);
  f.data.set(CHAT_HISTORY_KEY, "{");
  f.storage.removeItem = () => { throw new DOMException("Synthetic denial", "SecurityError"); };
  assert.match(loadChatHistory(scope, f.access).notice ?? "", /could not remove/i);
  assert.match(clearChatHistory(f.access) ?? "", /could not remove/i);
  const unsaved = saveChatHistory(scope, turns, f.access);
  assert.equal(unsaved.saved, false);
  assert.match(unsaved.notice ?? "", /could not remove/i, "Write failure must not hide a subsequent removal failure.");
  assert.equal(f.data.size, 1, "Do not claim failed deletion succeeded.");
  const bug = () => { throw new TypeError("Unexpected implementation bug"); };
  assert.throws(() => loadChatHistory(scope, bug), TypeError);
  assert.throws(() => saveChatHistory(scope, turns, bug), TypeError);
  assert.throws(() => clearChatHistory(bug), TypeError);
});
