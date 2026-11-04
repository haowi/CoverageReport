// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

#include "mathutil.h"

int clamp(int value, int low, int high)
{
    if (value < low) {
        return low;
    }
    if (value > high) {
        return high;
    }
    return value;
}

int gcd(int a, int b)
{
    while (b != 0) {
        int t = a % b;
        a = b;
        b = t;
    }
    return a;
}

/* Never called by the test driver: stays uncovered. */
int unused_add(int a, int b)
{
    return a + b;
}
