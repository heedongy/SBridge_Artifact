FROM mcr.microsoft.com/dotnet/sdk:9.0 AS dotnet
FROM ubuntu:22.04

# dotnet SDK (correct way)
COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet

# recreate symlink (IMPORTANT)
RUN ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet

ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH=$PATH:/usr/share/dotnet

# Set non-interactive frontend to avoid prompts during package installation

ENV DEBIAN_FRONTEND=noninteractive

RUN groupadd --gid 1000 user \
    && useradd --uid 1000 --gid user --shell /bin/bash --create-home user

RUN apt-get update && \
    apt-get install -y \
        python3 git clang unzip tar wget curl gcc universal-ctags \
        openjdk-17-jdk \
        apt-transport-https ca-certificates gnupg && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

RUN ln -sf python3 /usr/bin/python



USER user

RUN mkdir -p /home/user/tools
RUN mkdir -p /home/user/gridra_project

WORKDIR /home/user/tools

# Install sbt
ENV SBT_VERSION=1.10.3
ENV SBT_HOME=/home/user/tools/sbt
ENV PATH=${PATH}:${SBT_HOME}/bin

RUN curl -sL "https://github.com/sbt/sbt/releases/download/v$SBT_VERSION/sbt-$SBT_VERSION.tgz" | tar -xz -C /home/user/tools


# Download Ghidra
RUN wget -q https://github.com/NationalSecurityAgency/ghidra/releases/download/Ghidra_11.1.2_build/ghidra_11.1.2_PUBLIC_20240709.zip && \
    unzip ghidra_11.1.2_PUBLIC_20240709.zip && \
    rm ghidra_11.1.2_PUBLIC_20240709.zip


# Build Joern
RUN git clone --branch v4.0.206 --depth 1 https://github.com/joernio/joern.git
WORKDIR /home/user/tools/joern
RUN /home/user/tools/sbt/bin/sbt stage

WORKDIR /home/user/
ADD --chown=user:user SBridge /home/user/SBridge
ADD dataset.tar /home/user/
ADD script.tar /home/user/

WORKDIR /home/user/dataset/
RUN chmod +x copy_set.sh && ./copy_set.sh && rm -rf bin_feature copy_set.sh

WORKDIR /home/user/SBridge
RUN dotnet build -c Release
RUN chmod +x /home/user/SBridge/start_joernserver.sh

WORKDIR /home/user/
