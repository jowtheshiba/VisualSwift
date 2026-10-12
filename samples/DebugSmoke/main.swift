struct Counter {
    var value: Int
    mutating func add(_ amount: Int) {
        let before = value
        value += amount
        print("Counter: \(before) -> \(value)")
    }
}

var counter = Counter(value: 41)
counter.add(1)
print(counter.value)
