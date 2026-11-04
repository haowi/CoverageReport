// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

#ifndef MATHUTIL_H
#define MATHUTIL_H

#ifdef __cplusplus
extern "C" {
#endif

int clamp(int value, int low, int high);
int gcd(int a, int b);
int unused_add(int a, int b);

#ifdef __cplusplus
}
#endif

#endif
