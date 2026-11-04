#!/bin/sh
# SPDX-FileCopyrightText: 2026 Hanno Witt
# SPDX-License-Identifier: Apache-2.0

# Builds the sample project with coverage instrumentation, runs it, and regenerates
# tests/data/{gcov,gcov-plain,lcov,cobertura} from the result.
# Requires Linux or WSL with gcc, g++, gcov, lcov and gcovr.
set -eu

SAMPLE=$(cd "$(dirname "$0")" && pwd)
DATA="$SAMPLE/../tests/data"
BUILD=$(mktemp -d)
trap 'rm -rf "$BUILD"' EXIT

cd "$BUILD"
gcc --coverage -O0 -g -I"$SAMPLE/src" -c "$SAMPLE/src/mathutil.c" -o mathutil.o
g++ --coverage -O0 -g -I"$SAMPLE/src" -c "$SAMPLE/src/tokenizer.cpp" -o tokenizer.o
g++ --coverage -O0 -g -I"$SAMPLE/src" -c "$SAMPLE/src/main.cpp" -o main.o
g++ --coverage mathutil.o tokenizer.o main.o -o sample
./sample

rm -rf "$DATA/gcov" "$DATA/gcov-plain" "$DATA/lcov" "$DATA/cobertura"
mkdir -p "$DATA/gcov" "$DATA/gcov-plain" "$DATA/lcov" "$DATA/cobertura"

# gcov with branch and function data, mangled names.
gcov -b -c -f -p -o . mathutil.o tokenizer.o main.o > /dev/null
mv ./*.gcov "$DATA/gcov/"

# gcov without -b -f: line data only.
gcov -p -o . mathutil.o tokenizer.o main.o > /dev/null
mv ./*.gcov "$DATA/gcov-plain/"

lcov --capture --directory . --output-file "$DATA/lcov/sample.info" \
     --rc branch_coverage=1 --ignore-errors mismatch,inconsistent > /dev/null

gcovr --root "$SAMPLE/.." --filter '.*/sample/src/.*' --object-directory . \
      --cobertura-pretty --output "$DATA/cobertura/sample.xml" .

echo "Done: $DATA"
