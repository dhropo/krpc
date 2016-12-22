FROM ubuntu:16.04

RUN apt-get update && apt-get install -y --no-install-recommends \
    autoconf \
    automake \
    git \
    libenchant1c2a \
    librsvg2-bin \
    libtool \
    libxml2-dev \
    libxslt1-dev \
    luarocks \
    python-dev \
    python-setuptools \
    texlive-fonts-recommended \
    texlive-latex-base \
    texlive-latex-extra \
    texlive-latex-recommended \
    unzip \
    virtualenv \
    && rm -rf /var/lib/apt/lists/*

RUN apt-key adv \
      --keyserver hkp://keyserver.ubuntu.com:80 \
      --recv-keys 3FA7E0328081BFF6A14DA29AA6A19B38D3D831EF && \
    echo "deb http://download.mono-project.com/repo/debian wheezy main" | \
      tee /etc/apt/sources.list.d/mono-xamarin.list && \
    apt-get update && apt-get install -y \
    mono-devel \
    && rm -rf /var/lib/apt/lists/*

RUN apt-get update && apt-get install -y curl && \
    echo "deb [arch=amd64] http://storage.googleapis.com/bazel-apt stable jdk1.8" | \
      tee /etc/apt/sources.list.d/bazel.list && \
    curl https://bazel.build/bazel-release.pub.gpg | apt-key add - && \
    apt-get update && apt-get install -y \
    bazel=0.4.3 \
    && rm -rf /var/lib/apt/lists/*

RUN bazel version
COPY bazelrc /etc/bazel.bazelrc
WORKDIR /build
RUN groupadd -r -g 1000 build && useradd -r -u 1000 -g build -d /build build && chown build:build /build
USER build
