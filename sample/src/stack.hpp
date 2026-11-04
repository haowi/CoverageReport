// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

#ifndef STACK_HPP
#define STACK_HPP

#include <stdexcept>
#include <string>
#include <vector>

namespace demo {

template <typename T>
class Stack {
public:
    void push(const T& value) { items_.push_back(value); }

    T pop()
    {
        if (items_.empty()) {
            throw std::out_of_range("empty stack");
        }
        T top = items_.back();
        items_.pop_back();
        return top;
    }

    bool empty() const { return items_.empty(); }

    // Never instantiated by the test driver.
    void clear() { items_.clear(); }

private:
    std::vector<T> items_;
};

class Tokenizer {
public:
    explicit Tokenizer(const std::string& text);
    std::vector<std::string> split(char separator) const;
    size_t count(char separator) const;
    static bool isBlank(const std::string& text);

private:
    std::string text_;
};

}  // namespace demo

#endif
