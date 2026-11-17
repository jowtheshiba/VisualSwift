import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const revision = '88500a474365c5bda55588258cb585a1f275529e';
const response = await fetch(`https://raw.githubusercontent.com/jtbandes/swift-tmlanguage/${revision}/Swift.tmLanguage.json`);
if (!response.ok) throw new Error(`Upstream grammar download failed: ${response.status}`);
const original = await response.text();
if (crypto.createHash('sha256').update(original).digest('hex') !== '6b517f8403c77452978dddfd0d1d19a458fe1bf1055a9217f5e16848c19a0986') throw new Error('Upstream grammar checksum mismatch.');
await fs.mkdir(path.join(root, 'artifacts'), { recursive: true });
await fs.writeFile(path.join(root, 'artifacts/grammar-upstream.json'), original);
const grammar = JSON.parse(original);
function stripRegexComments(pattern) {
  if (!pattern.includes('(?x)')) return pattern;
  let result = '', depth = 0;
  for (let i = 0; i < pattern.length; i++) {
    const c = pattern[i];
    if (c === '\\') { result += c + (pattern[++i] ?? ''); continue; }
    if (c === '[') depth++;
    if (c === ']') depth = Math.max(0, depth - 1);
    if (c === '#' && depth === 0) { while (i < pattern.length && pattern[i] !== '\n') i++; result += '\n'; }
    else result += c;
  }
  return result;
}
function adapt(node) {
  if (!node || typeof node !== 'object') return;
  delete node.comment;
  for (const [key, value] of Object.entries(node)) {
    if (['match', 'begin', 'end', 'while'].includes(key) && typeof value === 'string') node[key] = stripRegexComments(value);
    else adapt(value);
  }
}
adapt(grammar);
grammar.fileTypes = ['swift', 'swiftinterface'];
grammar.repository['async-throws'] = { patterns: [
  { name: 'meta.throws-type.swift', begin: '\\b(throws)\\s*(\\()', end: '\\)', beginCaptures: { 1: { name: 'storage.modifier.exception.swift' }, 2: { name: 'punctuation.definition.parameters.begin.swift' } }, endCaptures: { 0: { name: 'punctuation.definition.parameters.end.swift' } }, patterns: [{ include: '#declarations-available-types' }] },
  grammar.repository['async-throws']
] };
grammar.repository['visualswift-module-selector'] = { match: '\\b([\\p{L}_][\\p{L}_\\p{N}\\p{M}]*)(::)', captures: { 1: { name: 'entity.name.type.module.swift' }, 2: { name: 'keyword.operator.module-selector.swift' } } };
for (const key of ['declarations-available-types', 'expressions-without-trailing-closures-or-member-references']) grammar.repository[key].patterns.unshift({ include: '#visualswift-module-selector' });
const strings = grammar.repository['literals-string'].patterns;
const rawMultiline = strings.find(rule => rule.begin.startsWith('#"""'));
const rawSingleLine = strings.find(rule => rule.begin === '#"');
const rawContents = grammar.repository['literals-string-raw-string-guts'].patterns;
const literalText = String.raw`[^"\\\s]+|[^\S\r\n]+|[^\r\n]`;
function isolateInterpolation(rule) {
  // Visual Studio inherits the outer string color for otherwise unclassified Swift code.
  rule.patterns.push({ name: rule.name, match: literalText });
  rule.name = 'meta.' + rule.name;
}

// TextMate only substitutes begin captures in end/while. Generate exact escape
// patterns for the delimiter lengths representable by Swift's 8-bit token field.
const rawRules = [];
for (let count = 1; count <= 255; count++) {
  const hashes = '#{' + count + '}';
  const withDelimiter = pattern => pattern.replaceAll('#', hashes);
  for (const template of [rawMultiline, rawSingleLine]) {
    const rule = structuredClone(template);
    rule.begin = '(?<!#)' + withDelimiter(rule.begin);
    rule.end = rule.end.replace('#', hashes);
    rule.patterns = rule.patterns.flatMap(pattern => {
      if (pattern.include === '#literals-string-raw-string-guts') {
        return rawContents.map(escape => escape.begin
          ? { ...escape, begin: withDelimiter(escape.begin) }
          : { ...escape, match: withDelimiter(escape.match).replace(hashes, hashes + '(?!#)') });
      }
      if (pattern.name === 'invalid.illegal.content-before-closing-delimiter.swift') {
        return [{ ...pattern, match: withDelimiter(pattern.match).replace('(?=""")', '(?="""' + hashes + ')') }];
      }
      if (pattern.name === 'constant.character.escape.newline.swift') return [{ ...pattern, match: withDelimiter(pattern.match) }];
      return [pattern];
    });
    isolateInterpolation(rule);
    rawRules.push(rule);
  }
}

const rawInclude = '#visualswift-raw-strings';
grammar.repository['literals-string'].patterns = strings.filter(rule => rule !== rawMultiline && rule !== rawSingleLine);
grammar.repository['literals-string'].patterns.splice(1, 0, { include: rawInclude });
for (const rule of strings.filter(rule => rule.patterns.some(pattern => pattern.include === '#literals-string-string-guts'))) isolateInterpolation(rule);
delete grammar.repository['literals-string-raw-string-guts'];
grammar.repository[rawInclude.slice(1)] = { patterns: '__VISUALSWIFT_RAW_RULES__' };
const generated = JSON.stringify(grammar, null, 2).replace('"__VISUALSWIFT_RAW_RULES__"',
  '[\n' + rawRules.map(rule => '        ' + JSON.stringify(rule)).join(',\n') + '\n      ]');
await fs.writeFile(path.join(root, 'src/VisualSwift.VisualStudio/Grammars/swift.tmLanguage.json'), generated + '\n');
console.log('Adapted pinned Swift TextMate grammar.');
