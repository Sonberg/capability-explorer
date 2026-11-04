export function parseStrictJson(text: string): unknown {
  const parsed: unknown = JSON.parse(text);
  const containers: Array<Set<string> | null> = [];
  for (const match of text.matchAll(/"(?:[^"\\]|\\[\s\S])*"|[{}\[\]]/g)) {
    const token = match[0];
    if (token === "{" || token === "[") {
      containers.push(token === "{" ? new Set() : null);
      if (containers.length > 32) throw new SyntaxError("JSON depth exceeds its bound");
    } else if (token === "}" || token === "]") {
      containers.pop();
    } else {
      const value = JSON.parse(token) as string;
      for (const character of value) {
        const code = character.codePointAt(0)!;
        if (code >= 0xd800 && code <= 0xdfff) throw new SyntaxError("JSON contains malformed Unicode");
      }
      let next = match.index + token.length;
      while (text[next] === " " || text[next] === "\t" || text[next] === "\r" || text[next] === "\n") next++;
      if (text[next] === ":") {
        const keys = containers.at(-1);
        if (!keys || keys.has(value)) throw new SyntaxError("JSON contains duplicate members");
        keys.add(value);
      }
    }
  }
  return parsed;
}
