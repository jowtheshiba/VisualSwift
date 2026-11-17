import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import textmate from 'vscode-textmate';
import oniguruma from 'vscode-oniguruma';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const require = createRequire(import.meta.url);
const wasm = await fs.readFile(require.resolve('vscode-oniguruma/release/onig.wasm'));
await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
const grammarPath = path.join(root, 'src/VisualSwift.VisualStudio/Grammars/swift.tmLanguage.json');
const registry = new textmate.Registry({
  onigLib: Promise.resolve({ createOnigScanner: patterns => new oniguruma.OnigScanner(patterns), createOnigString: text => new oniguruma.OnigString(text) }),
  loadGrammar: async scope => scope === 'source.swift' ? textmate.parseRawGrammar(await fs.readFile(grammarPath, 'utf8'), grammarPath) : null
});
const grammar = await registry.loadGrammar('source.swift');
const results = [];
function check(name, source, expectations) {
  let stack = textmate.INITIAL;
  const lines = source.split('\n');
  const tokens = lines.map(line => { const result = grammar.tokenizeLine(line, stack); stack = result.ruleStack; return result.tokens; });
  for (const [line, word, scope, occurrence = 0] of expectations) {
    let index = -1;
    for (let n = 0; n <= occurrence; n++) index = lines[line].indexOf(word, index + 1);
    if (index < 0) throw new Error(`${name}: missing marker ${word}`);
    const token = tokens[line].find(t => t.startIndex <= index && t.endIndex > index);
    const hasScope = prefix => token.scopes.some(s => s.startsWith(prefix));
    const matches = token && (scope.startsWith('=') ? token.scopes.at(-1) === scope.slice(1)
      : scope.startsWith('!') ? !hasScope(scope.slice(1)) : hasScope(scope));
    if (!matches) throw new Error(`${name}: ${word} expected ${scope}, got ${JSON.stringify(token)}`);
  }
  results.push({ name, passed: true, assertions: expectations.length, lines, tokens });
}
check('concurrency', 'actor Worker {\n nonisolated(nonsending) func work() async throws {\n  try await Task.sleep(for: .seconds(1))\n }\n}', [[0,'actor','storage.type'],[1,'nonisolated','storage.modifier'],[1,'async','storage.modifier'],[1,'throws','storage.modifier'],[2,'await','keyword.control']]);
check('ownership', 'struct Handle: ~Copyable {\n borrowing func read(_ input: borrowing Handle) {}\n consuming func close() {}\n}\nfunc transfer(_ value: sending Handle) {}', [[0,'~','keyword.operator'],[1,'borrowing','storage.modifier'],[2,'consuming','storage.modifier'],[4,'sending','storage.modifier']]);
check('packs', 'func items<each T>(_ values: repeat each T) {\n repeat print(each values)\n}', [[0,'each','keyword'],[0,'repeat','keyword'],[1,'repeat','keyword'],[1,'each','keyword']]);
check('macros-access-attributes', '@attached(member)\npublic macro Model() = #externalMacro(module: "Macros", type: "ModelMacro")\n@diagnose(warning, as: error)\npackage struct ModelValue {}\n@concurrent func run() async {}', [[0,'@attached','storage.modifier'],[1,'macro','storage.type'],[1,'#externalMacro','support'],[2,'@diagnose','storage.modifier'],[3,'package','keyword'],[4,'@concurrent','storage.modifier']]);
check('typed-throws-existentials', 'protocol Failure: Error {}\nfunc load() throws(any Error) -> any Failure? { fatalError() }\nfunc values() -> some Collection? { [1] }', [[1,'throws','storage.modifier'],[1,'any','keyword'],[1,'any','keyword',1],[2,'some','keyword']]);
check('literals', 'let raw = ##"literal \\(name) \\#(name) \\##(name)"##\nlet pattern = #/a+[0-9]/#\nlet short = /ab+/\nlet hex = 0x1.fp2\nlet binary = 0b1010_0011\nlet `actor` = "text"', [[0,'literal','string'],[0,'name','string'],[0,'name','string',1],[0,'name','!string',2],[1,'a+','string.regexp'],[2,'ab+','string.regexp'],[3,'0x1','constant.numeric'],[4,'0b','constant.numeric'],[5,'`actor`','=source.swift']]);
check('multiline-interpolation', 'let value = """\nhello \\(nested("text"))\n"""\nlet after = 1', [[1,'hello','string'],[1,'nested','support.function'],[1,'text','string'],[3,'let','keyword']]);
check('string-interpolation', String.raw`print("name: \(obj.name), \(nested("text", obj)) suffix \\(literal)")`, [
  [0, 'name:', 'string'], [0, '\\(', 'punctuation.section.embedded.begin'],
  [0, 'obj', '!string'], [0, '.name', '!string'], [0, 'name', 'variable.other', 1],
  [0, 'nested', 'support.function'], [0, 'text', 'string'], [0, 'obj', '!string', 1],
  [0, ') suffix', 'punctuation.section.embedded.end'], [0, 'suffix', 'string'], [0, '(literal)', 'string']
]);
check('raw-string-interpolation', String.raw`let s = #"\(literal) \#(obj.name) suffix"#
let block = #"""
    value: \#(obj.name) suffix
    """#`, [
  [0, 'literal', 'string'], [0, 'obj', '!string'], [0, 'suffix', 'string'],
  [2, 'value:', 'string'], [2, 'obj', '!string'], [2, 'suffix', 'string']
]);
check('extended-raw-interpolation', String.raw`let s = ##""# prefix \###(extra) \##(nested(#"\#(obj.name)"#)) suffix"##
let block = ###"""
    """## and \##(literal)
    value: \###(
        nested(1, obj.name)
    ) suffix
    """###
let after = 1`, [
  [0, 'prefix', 'string'], [0, 'extra', 'string'], [0, '\\##(', 'punctuation.section.embedded.begin'],
  [0, 'nested', 'support.function'], [0, 'obj', '!string'], [0, 'suffix', 'string'],
  [2, '"""##', 'string'], [2, 'literal', 'string'], [3, 'value:', 'string'],
  [4, '1', 'constant.numeric'], [4, 'obj', '!string'], [5, 'suffix', 'string'], [7, 'let', 'keyword']
]);
const longestDelimiter = '#'.repeat(255);
check('long-raw-delimiter', 'let x = ' + longestDelimiter + '"\\' + longestDelimiter + '(obj)"' + longestDelimiter,
  [[0, 'obj', '!string'], [0, 'obj', 'meta.embedded.line']]);
check('nested-comments', '/* outer\n /* nested */ still outer\n*/\nlet after = 1', [[0,'outer','comment'],[1,'nested','comment'],[1,'still','comment'],[3,'let','keyword']]);
check('directives-module-selection', '#if compiler(>=6.4)\nlet count = Swift::Int(1)\n#else\nlet count = 1\n#endif', [[0,'if','keyword'],[2,'else','keyword'],[4,'endif','keyword'],[1,'::','keyword.operator.module-selector'],[1,'Swift','entity.name.type.module']]);
await fs.mkdir(path.join(root, 'artifacts'), { recursive: true });
await fs.writeFile(path.join(root, 'artifacts/grammar-checks.json'), JSON.stringify({ passed: true, results }, null, 2));
console.log(`Swift grammar checks passed: ${results.length} cases, ${results.reduce((n,r)=>n+r.assertions,0)} scope assertions.`);
