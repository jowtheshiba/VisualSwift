struct ClosedFileGreeter {
    func greeting() -> String {
        Greeter().greeting(name: "another file")
    }
}
