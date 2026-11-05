// Read-only projection of authored DMN decision logic from an already preflighted DOM.
// FEEL is shown as raw textContent only: nothing is evaluated and no markup is interpreted.
const DMN_NS = 'https://www.omg.org/spec/DMN/20191111/MODEL/';
const MAX_RULES = 200;

const children = (element: Element, name: string) =>
  [...element.children].filter(child => child.namespaceURI === DMN_NS && child.localName === name);
const child = (element: Element, name: string) => children(element, name)[0];
const authoredText = (element: Element | undefined) => {
  const text = element && child(element, 'text');
  return text ? text.textContent ?? '' : undefined;
};

function cell(tag: 'td' | 'th', value: string | undefined, missing: string) {
  const element = document.createElement(tag);
  if (value === undefined || value === '') {
    element.textContent = missing;
    element.className = 'model-logic-missing';
  } else {
    const code = document.createElement('code');
    code.textContent = value;
    element.append(code);
  }
  return element;
}

function note(text: string, className = 'model-preview-note') {
  const element = document.createElement('p');
  element.className = className;
  element.textContent = text;
  return element;
}

function decisionTable(table: Element, decisionName: string): HTMLElement[] {
  const inputs = children(table, 'input');
  const outputs = children(table, 'output');
  const rules = children(table, 'rule');
  const hitPolicy = table.getAttribute('hitPolicy') || 'UNIQUE';
  const aggregation = table.getAttribute('aggregation');
  const summary = note(`Decision table · hit policy ${hitPolicy}${aggregation ? ` (${aggregation})` : ''} · ` +
    `${inputs.length} input${inputs.length === 1 ? '' : 's'}, ${outputs.length} output${outputs.length === 1 ? '' : 's'}, ` +
    `${rules.length} rule${rules.length === 1 ? '' : 's'}`);
  if (!inputs.length && !outputs.length) return [summary, note('The decision table has no authored columns.')];
  const misaligned = rules.find(rule =>
    children(rule, 'inputEntry').length !== inputs.length || children(rule, 'outputEntry').length !== outputs.length);
  if (misaligned) {
    return [summary, note(`Rule ${misaligned.getAttribute('id') || '(unnamed)'} does not match the table columns; ` +
      'rows are not shown to avoid misaligned cells.', 'model-preview-error')];
  }
  const wrapper = document.createElement('div');
  wrapper.className = 'model-logic-table';
  wrapper.tabIndex = 0;
  wrapper.setAttribute('role', 'group');
  wrapper.setAttribute('aria-label', `Authored rules for ${decisionName}`);
  const element = document.createElement('table');
  const caption = document.createElement('caption');
  caption.textContent = `Authored rules for ${decisionName} (static source, not evaluated)`;
  const head = document.createElement('thead');
  const header = document.createElement('tr');
  header.append(Object.assign(document.createElement('th'), { scope: 'col', textContent: '#' }));
  for (const input of inputs) {
    const th = cell('th', input.getAttribute('label') || authoredText(child(input, 'inputExpression')), '(unnamed input)');
    th.scope = 'col';
    th.classList.add('model-logic-input');
    header.append(th);
  }
  for (const output of outputs) {
    const th = cell('th', output.getAttribute('label') || output.getAttribute('name') || undefined, '(unnamed output)');
    th.scope = 'col';
    th.classList.add('model-logic-output');
    header.append(th);
  }
  const annotated = rules.some(rule => children(rule, 'annotationEntry').length);
  if (annotated) header.append(Object.assign(document.createElement('th'), { scope: 'col', textContent: 'Annotation' }));
  head.append(header);
  const body = document.createElement('tbody');
  rules.slice(0, MAX_RULES).forEach((rule, index) => {
    const row = document.createElement('tr');
    row.append(Object.assign(document.createElement('th'), { scope: 'row', textContent: String(index + 1) }));
    for (const entry of children(rule, 'inputEntry')) row.append(cell('td', authoredText(entry), '(no text)'));
    for (const entry of children(rule, 'outputEntry')) row.append(cell('td', authoredText(entry), '(no text)'));
    if (annotated) row.append(cell('td', authoredText(child(rule, 'annotationEntry')), '(none)'));
    body.append(row);
  });
  element.append(caption, head, body);
  wrapper.append(element);
  const result = [summary, wrapper];
  if (rules.length > MAX_RULES) result.push(note(`Showing the first ${MAX_RULES} of ${rules.length} rules.`));
  if (!rules.length) result.push(note('The decision table has no authored rules.'));
  return result;
}

/** Builds read-only logic nodes for exactly the selected decision; unrelated decisions are ignored. */
export function renderDecisionLogic(decision: Element): HTMLElement[] {
  const name = decision.getAttribute('name') || decision.getAttribute('id') || 'decision';
  const logic = [...decision.children].find(element => element.namespaceURI === DMN_NS &&
    !['description', 'extensionElements', 'question', 'allowedAnswers', 'variable', 'informationRequirement',
      'knowledgeRequirement', 'authorityRequirement', 'supportedObjective', 'impactedPerformanceIndicator',
      'decisionMaker', 'decisionOwner', 'usingProcess', 'usingTask'].includes(element.localName));
  if (!logic) return [note('This decision has no authored decision logic.')];
  if (logic.localName === 'decisionTable') return decisionTable(logic, name);
  if (logic.localName === 'literalExpression') {
    const text = authoredText(logic);
    const block = document.createElement('pre');
    block.className = 'model-logic-literal';
    block.setAttribute('aria-label', `Authored literal expression for ${name}`);
    if (text) block.textContent = text;
    else { block.textContent = '(no text)'; block.classList.add('model-logic-missing'); }
    return [note(`Literal expression${logic.getAttribute('typeRef') ? ` · ${logic.getAttribute('typeRef')}` : ''}`), block];
  }
  return [note(`Decision logic of type "${logic.localName}" is not previewed here. Open the decision for the full model.`)];
}
