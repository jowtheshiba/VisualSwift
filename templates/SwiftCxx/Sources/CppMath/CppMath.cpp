#include "CppMath.h"

CppMath::Calculator::Calculator(int initialValue) : value(initialValue) {}

int CppMath::Calculator::add(int amount) const {
    return value + amount;
}
