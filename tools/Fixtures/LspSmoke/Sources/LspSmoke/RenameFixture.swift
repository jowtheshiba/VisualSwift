final class DeliveryService {
    var completedCount = 0

    func deliver(message: String) -> String {
        let receipt = message
        completedCount += 1
        return receipt
    }

    func deliver(_ amount: Int) -> Int {
        amount
    }
}

func deliveryExample() -> String {
    let service = DeliveryService()
    return service.deliver(message: "DeliveryService completedCount receipt")
}

func unrelatedReceipt() -> String {
    let receipt = "unchanged"
    return receipt
}
