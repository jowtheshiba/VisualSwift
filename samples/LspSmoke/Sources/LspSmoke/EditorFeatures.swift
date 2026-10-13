protocol MessageProvider {
    func makeMessage(_ name: String, count: Int) -> String
}

struct DefaultMessageProvider: MessageProvider {
    func makeMessage(_ name: String, count: Int) -> String {
        String(repeating: name, count: count)
    }
}

func editorFeatureFixture() {
    let provider = DefaultMessageProvider()
    let number = 2
    let result = provider.makeMessage("Swift", count: number)
    print(result)
}
