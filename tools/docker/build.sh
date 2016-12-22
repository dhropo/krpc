#!/bin/bash

docker build . -t krpc/buildenv-bazel-0.4.3
docker push krpc/buildenv-bazel-0.4.3
