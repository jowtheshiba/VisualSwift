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
await fs.writeFile(path.join(root, 'src/VisualSwift.VisualStudio/Grammars/swift.tmLanguage.json'), JSON.stringify(grammar, null, 2) + '\n');
console.log('Adapted pinned Swift TextMate grammar.');
