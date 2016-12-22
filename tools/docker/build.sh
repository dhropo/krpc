#!/bin/bash

docker build . -t krpc/buildenv
docker push krpc/buildenv
