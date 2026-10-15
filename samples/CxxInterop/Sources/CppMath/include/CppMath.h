#pragma once

namespace CppMath {
class Calculator {
public:
    explicit Calculator(int initialValue);
    int add(int amount) const;

private:
    int value;
};
}
