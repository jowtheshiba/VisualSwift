protocol SyntaxPayload: Sendable {}

struct SyntaxMessage: SyntaxPayload {
    let value: Int
}

actor SyntaxWorker {
    nonisolated(nonsending) func send(_ value: sending SyntaxMessage) async -> SyntaxMessage {
        value
    }
}

struct SyntaxHandle: ~Copyable {
    borrowing func read() -> Int { 1 }
    consuming func close() {}
}

enum SyntaxDecodeError: Error {
    case invalid
}

func syntaxDecode(_ value: String) throws(SyntaxDecodeError) -> Swift::Int {
    guard let result = Int(value) else { throw .invalid }
    return result
}

func syntaxVisit<each T>(_ values: repeat each T) {
    repeat print(each values)
}

func syntaxOptionalAny() -> any SyntaxPayload? { nil }
func syntaxOptionalSome() -> some SyntaxPayload? { nil as SyntaxMessage? }

@concurrent func syntaxConcurrent() async {}

func syntaxLiterals(_ name: String) -> String {
    let regex = #/a+[0-9]/#
    let short = /ab+/
    let raw = ##"literal \(name) \#(name) \##(name)"##
    let multiline = """
    hello \(name.uppercased())
    """
    let hex = 0x1.fp2
    let binary = 0b1010_0011
    let `actor` = "text"
    _ = (regex, short, hex, binary, `actor`)
    return raw + multiline
}
