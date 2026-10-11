struct Greeter {
    func greeting(name: String) -> String {
        "Hello, \(name)!"
    }
}

let greeter = Greeter()
let message = greeter.greeting(name: "Visual Studio")
print(message)
