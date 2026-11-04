// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

#include "stack.hpp"

namespace demo {

Tokenizer::Tokenizer(const std::string& text) : text_(text) {}

std::vector<std::string> Tokenizer::split(char separator) const
{
    std::vector<std::string> parts;
    std::string current;
    for (char c : text_) {
        if (c == separator) {
            parts.push_back(current);
            current.clear();
        } else {
            current += c;
        }
    }
    if (!current.empty()) {
        parts.push_back(current);
    }
    return parts;
}

// Never called by the test driver: stays uncovered.
size_t Tokenizer::count(char separator) const
{
    size_t total = 0;
    for (char c : text_) {
        if (c == separator) {
            ++total;
        }
    }
    return total;
}

bool Tokenizer::isBlank(const std::string& text)
{
    return text.find_first_not_of(" \t") == std::string::npos;
}

}  // namespace demo
