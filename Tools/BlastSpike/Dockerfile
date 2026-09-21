# Linux build of blast_cut. Run fetch_blast.sh on the host first.
#   docker build -t blast-cut Tools/BlastSpike
#   docker run --rm -v "$PWD/Tools/BlastSpike/data:/data" blast-cut /data/tower.obj /data/out.obj voronoi 40 1
FROM ubuntu:24.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      g++ cmake make libboost-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /spike
COPY CMakeLists.txt blast_cut.cpp ./
COPY macos-shim ./macos-shim
COPY blast ./blast
RUN cmake -S . -B build && cmake --build build -j"$(nproc)"
ENTRYPOINT ["/spike/build/blast_cut"]
