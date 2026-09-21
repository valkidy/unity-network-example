// Spike: fracture an OBJ with Blast's ExtAuthoring and write every leaf chunk
// back out as an OBJ group, with the faces Blast created on the cuts marked as
// the "interior" material.
//
//   blast_cut in.obj out.obj voronoi <cells> <seed>
//   blast_cut in.obj out.obj slice <x> <y> <z> <noiseAmplitude> <noiseFrequency> <seed> [samplingInterval]

#include "NvBlastExtAuthoring.h"
#include "NvBlastExtAuthoringFractureTool.h"
#include "NvBlastExtAuthoringMesh.h"
#include "NvBlastExtAuthoringTypes.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <random>
#include <sstream>
#include <string>
#include <map>
#include <vector>

using namespace Nv::Blast;

namespace
{
struct Rng : RandomGeneratorBase
{
    std::mt19937 generator;
    std::uniform_real_distribution<float> unit{ 0.f, 1.f };
    float getRandomValue() override { return unit(generator); }
    void seed(int32_t value) override { generator.seed(static_cast<uint32_t>(value)); }
};

struct ObjMesh
{
    std::vector<NvcVec3> positions;
    std::vector<NvcVec3> normals;
    std::vector<NvcVec2> uvs;
    std::vector<uint32_t> indices;
};

// Reads the OBJ export_glb_to_obj.py writes: v/vn/vt with one shared index per corner.
bool readObj(const char* path, ObjMesh& mesh)
{
    std::ifstream in(path);
    if (!in)
    {
        return false;
    }

    std::string line;
    while (std::getline(in, line))
    {
        std::istringstream s(line);
        std::string tag;
        s >> tag;
        if (tag == "v")
        {
            NvcVec3 p; s >> p.x >> p.y >> p.z; mesh.positions.push_back(p);
        }
        else if (tag == "vn")
        {
            NvcVec3 n; s >> n.x >> n.y >> n.z; mesh.normals.push_back(n);
        }
        else if (tag == "vt")
        {
            NvcVec2 t; s >> t.x >> t.y; mesh.uvs.push_back(t);
        }
        else if (tag == "f")
        {
            for (int corner = 0; corner < 3; ++corner)
            {
                std::string ref; s >> ref;
                mesh.indices.push_back(static_cast<uint32_t>(std::atoi(ref.c_str()) - 1));
            }
        }
    }

    return !mesh.positions.empty() && mesh.normals.size() == mesh.positions.size() &&
        mesh.uvs.size() == mesh.positions.size();
}

double secondsSince(std::chrono::steady_clock::time_point start)
{
    return std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
}
}  // namespace

int main(int argc, char** argv)
{
    if (argc < 5)
    {
        std::fprintf(stderr,
            "usage: blast_cut in.obj out.obj voronoi <cells> <seed>\n"
            "       blast_cut in.obj out.obj slice <x> <y> <z> <noiseAmplitude> <noiseFrequency> <seed> [samplingInterval]\n");
        return 2;
    }

    ObjMesh source;
    if (!readObj(argv[1], source))
    {
        std::fprintf(stderr, "could not read %s\n", argv[1]);
        return 1;
    }

    std::printf("input: %zu vertices, %zu triangles\n", source.positions.size(), source.indices.size() / 3);
    auto start = std::chrono::steady_clock::now();
    Mesh* mesh = NvBlastExtAuthoringCreateMesh(source.positions.data(), source.normals.data(), source.uvs.data(),
        static_cast<uint32_t>(source.positions.size()), source.indices.data(),
        static_cast<uint32_t>(source.indices.size()));
    if (mesh == nullptr)
    {
        std::fprintf(stderr, "Blast refused the mesh\n");
        return 1;
    }

    FractureTool* tool = NvBlastExtAuthoringCreateFractureTool();
    std::printf("open edges in input: %s\n", tool->isMeshContainOpenEdges(mesh) ? "yes" : "no");
    tool->setInteriorMaterialId(1);
    tool->setRemoveIslands(true);
    const Mesh* meshes[] = { mesh };
    tool->setSourceMeshes(meshes, 1);

    Rng rng;
    const std::string mode = argv[3];
    int32_t result = -1;
    if (mode == "voronoi" && argc >= 6)
    {
        const uint32_t cells = static_cast<uint32_t>(std::atoi(argv[4]));
        rng.seed(std::atoi(argv[5]));
        VoronoiSitesGenerator* sites = NvBlastExtAuthoringCreateVoronoiSitesGenerator(mesh, &rng);
        sites->uniformlyGenerateSitesInMesh(cells);
        const NvcVec3* points = nullptr;
        const uint32_t count = sites->getVoronoiSites(points);
        std::printf("voronoi: %u sites\n", count);
        result = tool->voronoiFracturing(0, count, points, false);
        sites->release();
    }
    else if (mode == "slice" && argc >= 10)
    {
        SlicingConfiguration conf;
        conf.x_slices = std::atoi(argv[4]);
        conf.y_slices = std::atoi(argv[5]);
        conf.z_slices = std::atoi(argv[6]);
        conf.noise.amplitude = static_cast<float>(std::atof(argv[7]));
        conf.noise.frequency = static_cast<float>(std::atof(argv[8]));
        conf.noise.octaveNumber = 3;
        // The grid the noisy cut surface is sampled on, in the input's units. It
        // is what the cut faces' triangle count scales with.
        const float sampling = argc >= 11 ? static_cast<float>(std::atof(argv[10])) : 0.1f;
        conf.noise.samplingInterval = { sampling, sampling, sampling };
        rng.seed(std::atoi(argv[9]));
        std::printf("slice: %d x %d x %d, noise %.3f @ %.3f\n", conf.x_slices, conf.y_slices, conf.z_slices,
            conf.noise.amplitude, conf.noise.frequency);
        result = tool->slicing(0, conf, false, &rng);
    }
    else
    {
        std::fprintf(stderr, "unknown mode or missing arguments\n");
        return 2;
    }

    if (result != 0)
    {
        std::fprintf(stderr, "fracturing failed: %d\n", result);
        return 1;
    }

    tool->finalizeFracturing();
    std::printf("fractured in %.2f s\n", secondsSince(start));

    std::ofstream out(argv[2]);
    // Nine significant digits round-trip a float; the stream's default six
    // would move every vertex by up to a tenth of a millimetre at tower scale.
    out << std::setprecision(9);
    out << "mtllib blast_cut.mtl\n";
    size_t written = 0;
    size_t leaves = 0;
    size_t totalTriangles = 0;
    size_t interiorTriangles = 0;
    std::map<int32_t, size_t> materials;
    uint32_t smallest = UINT32_MAX;
    uint32_t largest = 0;
    for (uint32_t info = 0; info < tool->getChunkCount(); ++info)
    {
        if (!tool->getChunkInfo(static_cast<int32_t>(info)).isLeaf)
        {
            continue;
        }

        Triangle* triangles = nullptr;
        const uint32_t count = tool->getBaseMesh(static_cast<int32_t>(info), triangles);
        if (count == 0)
        {
            continue;
        }

        ++leaves;
        totalTriangles += count;
        smallest = count < smallest ? count : smallest;
        largest = count > largest ? count : largest;
        out << "g chunk_" << leaves - 1 << "\n";
        int material = -1;
        for (uint32_t t = 0; t < count; ++t)
        {
            const Triangle& tri = triangles[t];
            ++materials[tri.materialId];
            // The source mesh is all material 0; anything else is a face the cut made.
            if (tri.materialId != 0)
            {
                ++interiorTriangles;
            }

            if (tri.materialId != material)
            {
                material = tri.materialId;
                out << "usemtl " << (material != 0 ? "interior" : "exterior") << "\n";
            }

            const Vertex* corners[3] = { &tri.a, &tri.b, &tri.c };
            for (const Vertex* v : corners)
            {
                out << "v " << v->p.x << " " << v->p.y << " " << v->p.z << "\n";
                out << "vn " << v->n.x << " " << v->n.y << " " << v->n.z << "\n";
                out << "vt " << v->uv[0].x << " " << v->uv[0].y << "\n";
            }

            const size_t base = written + 1;
            out << "f " << base << "/" << base << "/" << base << " " << base + 1 << "/" << base + 1 << "/"
                << base + 1 << " " << base + 2 << "/" << base + 2 << "/" << base + 2 << "\n";
            written += 3;
        }

        delete[] triangles;
    }

    std::printf("leaf chunks: %zu, triangles: %zu (interior %zu), smallest %u, largest %u\n", leaves,
        totalTriangles, interiorTriangles, smallest, largest);
    for (const auto& m : materials)
    {
        std::printf("  material %d: %zu triangles\n", m.first, m.second);
    }
    std::printf("total %.2f s -> %s\n", secondsSince(start), argv[2]);
    tool->release();
    mesh->release();
    return 0;
}
