#!/bin/bash
set -ev

docker run \
  -v `pwd`:/build/krpc \
  -e "TRAVIS_BRANCH=${TRAVIS_BRANCH}" \
  -e "TRAVIS_PULL_REQUEST=${TRAVIS_PULL_REQUEST}" \
  -e "TRAVIS_JOB_NUMBER=${TRAVIS_JOB_NUMBER}" \
  -t -i krpc/buildenv /bin/bash -c \
  "pwd && ls -alh && cd krpc && ls -alh && bazel build //:krpc //doc:html //doc:compile-scripts //:csproj //tools/krpctools //tools/TestServer:archive && xbuild KRPC.sln && bazel test //:ci-test && tools/dist/genfiles.sh && tools/travis-ci/before-deploy.sh"

