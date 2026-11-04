// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

#include <cstdio>

#include "mathutil.h"
#include "stack.hpp"

int main()
{
    int sum = clamp(5, 0, 3) + clamp(-1, 0, 3) + gcd(12, 18);

    demo::Stack<int> numbers;
    numbers.push(sum);
    numbers.push(1);
    sum += numbers.pop();

    demo::Stack<std::string> words;
    words.push("<script>alert('not escaped')</script>");  // HTML escaping check in the report
    words.pop();

    demo::Tokenizer tokenizer("a,b,,c");
    sum += static_cast<int>(tokenizer.split(',').size());
    sum += demo::Tokenizer::isBlank(" ") ? 1 : 0;

    if (sum < 0) {
        std::printf("unreachable\n");
    }
    std::printf("%d\n", sum);
    return 0;
}
