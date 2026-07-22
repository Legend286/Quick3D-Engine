#define TINYGLTF_IMPLEMENTATION
#define STB_IMAGE_IMPLEMENTATION
#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "tiny_gltf.h"

#include <iostream>
#include <fstream>
#include <vector>
#include <string>
#include <cstdlib>
#include <filesystem>
#include <algorithm>
#include <limits>
#include <future>
#include <mutex>
#include <chrono>

namespace fs = std::filesystem;

struct Vertex {
    float px, py, pz;
    float nx, ny, nz;
    float tu, tv;
    float tx, ty, tz, tw;
};

struct MeshHeader {
    uint32_t magic;      // 'MSH1'
    uint32_t vertex_count;
    uint32_t index_count;
    uint32_t index_format; // 16 or 32
};

// Resolved at startup by main(). Empty means "we never located basisu" and
// ExecuteBasisu will refuse to spawn it. See ResolveBasisuPath for the
// resolution order (CLI override > self-discovery > bounded ancestor walk
// > env var > CWD legacy).
static std::string g_basisu_path;

// Minimum plausible file size for a KTX2 file. The Khronos KTX2 spec requires
// the 80-byte base header (12-byte identifier + 68 bytes of
// format/dimension/levelCount/etc.) before any level data. Anything smaller is
// rejected by the verify-before-sidecar guard so it doesn't masquerade as a
// valid runtime texture.
constexpr std::size_t kMinKtx2FileSize = 80;

// Walk `<tex_out_dir>/*.tex` and remove any sidecar whose `.ktx2` neighbour is
// missing or truncated. Used by the early-exit paths in main() to scrub stale
// lying sidecars left over from a previously-failed cook. The texture-phase
// lambda uses its own fail_with_cleanup for individual sidecars.
static void ScrubOrphanSidecars(const fs::path& tex_out_dir) {
    std::error_code ec;
    if (!fs::exists(tex_out_dir, ec) || !fs::is_directory(tex_out_dir, ec)) return;
    for (auto it = fs::directory_iterator(tex_out_dir, ec); !ec && it != fs::end(it); it.increment(ec)) {
        const auto& entry = *it;
        if (entry.path().extension() != ".tex") continue;
        fs::path ktx2 = entry.path();
        ktx2.replace_extension(".ktx2");
        std::error_code ec_exists, ec_sz;
        bool exists = fs::exists(ktx2, ec_exists);
        auto sz = exists ? fs::file_size(ktx2, ec_sz) : 0;
        bool orphan = !exists || ec_sz || sz < kMinKtx2FileSize;
        if (orphan) {
            std::error_code ec_rm;
            fs::remove(entry.path(), ec_rm);
            std::cerr << "WARN: removed orphan sidecar " << entry.path()
                      << " (ktx2 missing or < " << kMinKtx2FileSize << " bytes)\n";
        }
    }
}

static std::string ResolveBasisuPath(const char* self_argv0, const std::string& cli_override) {
    // 1. --basisu-path <abs> (CLI override, e.g. when running cook out-of-tree)
    if (!cli_override.empty()) {
        std::error_code ec;
        fs::path abs = fs::absolute(cli_override, ec);
        if (!ec && fs::exists(abs)) return abs.string();
    }

    // 2. Self-discovery: engine_cook lives at <engine_root>/out/engine_cook.
    //    realpath(argv[0]) -> outs/<engine>/out/engine_cook; parent.parent()
    //    -> <engine_root>; candidate <root>/out/basisu. Works when the
    //    editor or a script invokes the engine's own cook binary directly.
    //    Bounded to a 4-level ancestor walk to recover from unusual staging
    //    (e.g. CI bundles, sandboxed packaging where cook is symlinked
    //    through an intermediate path) by looking for the engine mark
    //    `engine_cs/` near a directory containing `out/basisu`.
    if (self_argv0 && self_argv0[0] != '\0') {
        char* self = realpath(self_argv0, nullptr);
        if (self) {
            // NOTE: must use brace-init here; the paren form `fs::path start(fs::path(self))`
            // is a most-vexing-parse (interpreted as a function declaration).
            fs::path dir{fs::path(self).parent_path()};
            std::free(self);
            for (int depth = 0; depth < 4 && !dir.empty(); ++depth) {
                fs::path cand = dir / "out" / "basisu";
                // Marker: prefer paths that are flagged as engine root
                // (`engine_cs/` adjacent), or live directly inside an
                // `out/` sibling of the engine root (e.g. staged packaging
                // where cook is at /staging/engine/out/engine_cook and root
                // is /staging/engine/).
                if (fs::exists(cand) &&
                    (fs::exists(dir / "engine_cs") || dir.filename() == "out"))
                    return cand.string();
                fs::path parent = dir.parent_path();
                if (parent == dir) break;
                dir = parent;
            }
        }
    }

    // 3. $QUICK3D_ENGINE_ROOT/out/basisu (env hint, useful for CI / sandboxed packaging)
    const char* env = std::getenv("QUICK3D_ENGINE_ROOT");
    if (env && env[0] != '\0') {
        fs::path cand = fs::path(env) / "out" / "basisu";
        if (fs::exists(cand)) return cand.string();
    }

    // 4. ./out/basisu (CWD-relative legacy fallback; only correct when CWD
    //    is the engine root, which used to be the original assumption).
    if (fs::exists("./out/basisu")) {
        std::error_code ec;
        return fs::absolute("./out/basisu", ec).string();
    }

    return "";
}

std::string ExecuteBasisu(const std::string& input_img, const std::string& out_dir, bool is_normal, bool is_linear, int width, int height, int channels, const std::string& type_name) {
    std::string tex_out_dir = (fs::path(out_dir) / "textures").string();
    fs::create_directories(tex_out_dir);
    // basisu defaults to ETC1S+BasisLZ (scheme=1, vkFormat=0), which the
    // runtime Ktx2Loader cannot decode (Basis Universal transcoder is not
    // wired in). -ktx2 -uastc forces ASTC_4x4_UNORM_BLOCK blocks (vkFormat=157)
    // and Zstd supercompression by default (scheme=3), both of which the
    // loader handles natively. See docs/asset-pipeline/cook.md.
    if (g_basisu_path.empty()) {
        std::cerr << "ERROR: basisu binary path not resolved before texture phase. Re-run engine_cook with --basisu-path or set QUICK3D_ENGINE_ROOT.\n";
        return "";
    }

    std::string cmd = "\"" + g_basisu_path + "\" -ktx2 -uastc \"" + input_img + "\" -output_path \"" + tex_out_dir + "\"";
    if (is_normal) cmd += " -normal_map";
    if (is_linear) cmd += " -linear";
    std::cout << "Running: " << cmd << "\n";
    int ret = std::system(cmd.c_str());
    fs::path p(input_img);
    std::string base_name = p.stem().string();
    std::string ktx2_path = (fs::path(tex_out_dir) / base_name).string() + ".ktx2";
    std::string tex_meta_path = (fs::path(tex_out_dir) / base_name).string() + ".tex";

    // On any failure: scrub any pre-existing sidecar so a previously
    // failed cook's lying sidecar can't survive a re-cook. The new cook
    // can't overwrite it because the sidecar-write is correctly gated
    // behind a verified .ktx2.
    auto fail_with_cleanup = [&](const std::string& why) -> std::string {
        std::error_code ec_rm;
        fs::remove(tex_meta_path, ec_rm);  // ignore ec_rm (file may not exist)
        std::cerr << "ERROR: " << why << " (input=" << input_img << ", expected=" << ktx2_path << ")\n";
        return "";
    };

    if (ret != 0) {
        return fail_with_cleanup(std::string("basisu failed with exit code ") + std::to_string(ret));
    }

    // Verify the actual .ktx2 exists and is non-trivial before writing any
    // metadata sidecar. The original version wrote the .tex JSON
    // unconditionally, which produced lying sidecars whenever basisu
    // failed (binary not on PATH, library mismatch, etc.) and the runtime
    // loader then silently nulled the texture (black / untextured material).
    {
        std::error_code ec_exists;
        if (!fs::exists(ktx2_path, ec_exists)) {
            return fail_with_cleanup("basisu reported success but .ktx2 missing");
        }
        std::error_code ec_size;
        auto sz = fs::file_size(ktx2_path, ec_size);
        if (ec_size || sz < kMinKtx2FileSize) {
            return fail_with_cleanup(
                std::string(".ktx2 is suspiciously small (size=") +
                std::to_string(sz) + ", ec=\"" + ec_size.message() + "\")");
        }
    }

    // Write out .tex metadata (only after the verified .ktx2 is on disk).
    std::ofstream tex_meta(tex_meta_path);
    tex_meta << "{\n";
    tex_meta << "  \"version\": 1,\n";
    tex_meta << "  \"type\": \"" << type_name << "\",\n";
    tex_meta << "  \"width\": " << width << ",\n";
    tex_meta << "  \"height\": " << height << ",\n";
    tex_meta << "  \"channels\": " << channels << ",\n";
    tex_meta << "  \"format\": \"ktx2\"\n";
    tex_meta << "}\n";
    tex_meta.close();

    // Return the relative filename for serialization in materials (only on success)
    return "../../textures/" + base_name + ".ktx2";
}

int main(int argc, char** argv) {
    if (argc < 2) {
        std::cerr << "Usage: engine_cook <input.glb/gltf> [out_dir]\n";
        return 1;
    }

    std::string input_file = argv[1];
    fs::path in_path(input_file);
    std::string out_dir = (argc >= 3 && argv[2][0] != '-') ? argv[2] : in_path.parent_path().string();
    if (out_dir.empty()) out_dir = ".";
    
    float scale_x = 1.0f, scale_y = 1.0f, scale_z = 1.0f;
    std::string cli_basisu;
    for (int i = 2; i < argc; ++i) {
        std::string arg = argv[i];
        if (arg == "-scale" && i + 3 < argc) {
            scale_x = std::stof(argv[i+1]);
            scale_y = std::stof(argv[i+2]);
            scale_z = std::stof(argv[i+3]);
            i += 3;
        }
        // --basisu-path is passed through /bin/sh -c by std::system as a
        // string interpolation. We deliberately do NOT sanitise shell
        // metacharacters; this is a developer CLI so the path is trusted
        // input. If a future caller delegates it to non-trusted sources,
        // switch to fork+execv with an argv array.
        else if (arg == "--basisu-path" && i + 1 < argc) {
            cli_basisu = argv[++i];
        }
    }

    g_basisu_path = ResolveBasisuPath(argc > 0 ? argv[0] : nullptr, cli_basisu);
    if (g_basisu_path.empty()) {
        std::cerr << "ERROR: cannot locate basisu binary. Tried in order:\n"
                  << "  1. --basisu-path <abs path>            (CLI override)\n"
                  << "  2. <engine_root>/out/basisu             (auto-discovered from engine_cook's location)\n"
                  << "  3. $QUICK3D_ENGINE_ROOT/out/basisu       (env hint)\n"
                  << "  4. ./out/basisu                         (CWD-relative legacy fallback)\n"
                  << "Set --basisu-path or QUICK3D_ENGINE_ROOT and re-run.\n";
        ScrubOrphanSidecars(fs::path(out_dir) / "textures");
        return 2;
    }
    std::cout << "Using basisu: " << g_basisu_path << "\n";
    
    fs::create_directories(fs::path(out_dir) / "models");
    fs::create_directories(fs::path(out_dir) / "models" / "materials");
    fs::create_directories(fs::path(out_dir) / "textures");

    std::string base_name = in_path.stem().string();
    std::string output_msh = (fs::path(out_dir) / (base_name + ".msh")).string();
    std::string output_mdl = (fs::path(out_dir) / (base_name + ".mdl")).string();

    tinygltf::Model model;
    tinygltf::TinyGLTF loader;
    std::string err, warn;

    bool ret = false;
    if (in_path.extension() == ".glb") {
        ret = loader.LoadBinaryFromFile(&model, &err, &warn, input_file);
    } else {
        ret = loader.LoadASCIIFromFile(&model, &err, &warn, input_file);
    }
    
    if (!warn.empty()) std::cout << "Warn: " << warn << "\n";
    if (!err.empty()) std::cerr << "Err: " << err << "\n";
    if (!ret) {
        std::cerr << "Failed to load " << input_file << "\n";
        ScrubOrphanSidecars(fs::path(out_dir) / "textures");
        return 1;
    }

    int total_tasks = (int)model.images.size();
    int defaultScene = model.defaultScene > -1 ? model.defaultScene : 0;
    if (defaultScene >= 0 && defaultScene < model.scenes.size()) {
        const tinygltf::Scene& scene = model.scenes[defaultScene];
        std::function<void(int)> count_traverse = [&](int node_idx) {
            if (node_idx < 0 || node_idx >= model.nodes.size()) return;
            const tinygltf::Node& node = model.nodes[node_idx];
            if (node.mesh >= 0 && node.mesh < model.meshes.size()) {
                total_tasks += model.meshes[node.mesh].primitives.size();
            }
            for (int child_idx : node.children) {
                count_traverse(child_idx);
            }
        };
        for (int root_idx : scene.nodes) {
            count_traverse(root_idx);
        }
    }
    
    std::atomic<int> g_progress_current{0};
    std::string g_progress_stage = "";
    int g_progress_total = 0;
    std::mutex g_progress_mutex;
    
    auto set_progress_stage = [&](const std::string& stage, int total) {
        std::lock_guard<std::mutex> lock(g_progress_mutex);
        g_progress_stage = stage;
        g_progress_total = total;
        g_progress_current.store(0, std::memory_order_relaxed);
    };

    auto report_progress = [&]() {
        int current = g_progress_current.fetch_add(1, std::memory_order_relaxed) + 1;
        std::lock_guard<std::mutex> lock(g_progress_mutex);
        std::cout << "[PROGRESS] " << g_progress_stage << "|" << current << "|" << g_progress_total << std::endl;
    };    
    // 0. Discover texture types
    enum class TexType { Albedo, Normal, RMA };
    std::vector<TexType> tex_types(model.images.size(), TexType::Albedo); // Default Albedo
    for (const auto& mat : model.materials) {
        if (mat.pbrMetallicRoughness.baseColorTexture.index >= 0) {
            int tex_idx = model.textures[mat.pbrMetallicRoughness.baseColorTexture.index].source;
            if (tex_idx >= 0) tex_types[tex_idx] = TexType::Albedo;
        }
        if (mat.normalTexture.index >= 0) {
            int tex_idx = model.textures[mat.normalTexture.index].source;
            if (tex_idx >= 0) tex_types[tex_idx] = TexType::Normal;
        }
        if (mat.pbrMetallicRoughness.metallicRoughnessTexture.index >= 0) {
            int tex_idx = model.textures[mat.pbrMetallicRoughness.metallicRoughnessTexture.index].source;
            if (tex_idx >= 0) tex_types[tex_idx] = TexType::RMA;
        }
    }

    // 1. Process Images & BasisU compress
    std::vector<std::string> cooked_textures(model.images.size());
    std::vector<std::future<void>> texture_futures;
    std::mutex tex_log_mutex;
    std::atomic<int> texture_failure_count{0};

    unsigned int num_threads = std::thread::hardware_concurrency();
    if (num_threads == 0) num_threads = 4;

    std::mutex tex_job_mutex;
    size_t current_tex = 0;
    
    set_progress_stage("Importing Textures", model.images.size());

    for (unsigned int t = 0; t < num_threads; ++t) {
        texture_futures.push_back(std::async(std::launch::async, [&]() {
            while (true) {
                size_t i;
                {
                    std::lock_guard<std::mutex> lock(tex_job_mutex);
                    if (current_tex >= model.images.size()) return;
                    i = current_tex++;
                }

                auto& img = model.images[i];
                std::string temp_png = (fs::temp_directory_path() / (base_name + "_tex_" + std::to_string(i) + "_" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()) + ".png")).string();
                std::string input_img;
                int w = 0, h = 0, comp = 0;
                
                if (img.image.size() > 0) {
                    stbi_write_png(temp_png.c_str(), img.width, img.height, img.component, img.image.data(), img.width * img.component);
                    input_img = temp_png;
                    w = img.width; h = img.height; comp = img.component;
                } else if (!img.uri.empty()) {
                    fs::path ext_path = in_path.parent_path() / img.uri;
                    unsigned char* raw = stbi_load(ext_path.string().c_str(), &w, &h, &comp, 0);
                    if (raw) {
                        stbi_image_free(raw);
                        input_img = ext_path.string();
                    } else {
                        std::lock_guard<std::mutex> lock(tex_log_mutex);
                        std::cerr << "Failed to load external texture " << ext_path << "\n";
                        continue;
                    }
                } else {
                    continue;
                }
                
                bool is_normal = (tex_types[i] == TexType::Normal);
                bool is_linear = (tex_types[i] == TexType::Normal || tex_types[i] == TexType::RMA);
                std::string type_name = "albedo";
                if (tex_types[i] == TexType::Normal) type_name = "normal";
                if (tex_types[i] == TexType::RMA) type_name = "rma";
                
                std::string out_ktx2;
                try {
                    out_ktx2 = ExecuteBasisu(input_img, out_dir, is_normal, is_linear, w, h, comp, type_name);
                    // Delete temp png if we extracted it
                    if (input_img == temp_png) {
                        std::error_code ec;
                        fs::remove(temp_png, ec);
                    }
                }
                catch (const std::exception& ex) {
                    std::lock_guard<std::mutex> lock(tex_log_mutex);
                    std::cerr << "ERROR: texture worker " << i << " threw: " << ex.what() << "\n";
                    texture_failure_count.fetch_add(1, std::memory_order_relaxed);
                    out_ktx2 = "";
                }
                catch (...) {
                    std::lock_guard<std::mutex> lock(tex_log_mutex);
                    std::cerr << "ERROR: texture worker " << i << " threw non-std exception\n";
                    texture_failure_count.fetch_add(1, std::memory_order_relaxed);
                    out_ktx2 = "";
                }
                cooked_textures[i] = out_ktx2;
                report_progress();
            }
        }));
    }

    for (auto& fut : texture_futures) {
        fut.get();
    }

    int failed = texture_failure_count.load(std::memory_order_relaxed);
    if (failed > 0) {
        std::cerr << "ERROR: " << failed << " texture(s) failed to cook. Material files will omit those references. Re-run with --basisu-path set to a working binary to recover.\n";
        // Defensive: per-texture fail_with_cleanup scrubbed the specific
        // sidecars, but a sibling .ktx2 may have been truncated by ENOSPC
        // or a process SIGKILL between writes. Walk the directory once more
        // so the editor never sees a fresh lying sidecar.
        ScrubOrphanSidecars(fs::path(out_dir) / "textures");
        return 3;
    }

    // 2. Process Materials
    std::vector<std::string> cooked_materials;
    for (size_t i = 0; i < model.materials.size(); ++i) {
        auto& mat = model.materials[i];
        std::string mat_name = mat.name.empty() ? (base_name + "_mat_" + std::to_string(i)) : mat.name;
        std::string output_mat = (fs::path(out_dir) / "models" / "materials" / (mat_name + ".mat")).string();
        cooked_materials.push_back("materials/" + mat_name + ".mat");

        std::ofstream mat_file(output_mat);
        mat_file << "{\n";
        mat_file << "  \"version\": 1,\n";
        mat_file << "  \"albedo_color\": [" 
                 << mat.pbrMetallicRoughness.baseColorFactor[0] << ", "
                 << mat.pbrMetallicRoughness.baseColorFactor[1] << ", "
                 << mat.pbrMetallicRoughness.baseColorFactor[2] << ", "
                 << mat.pbrMetallicRoughness.baseColorFactor[3] << "],\n";
                 
        // Bound on cooked_textures and skip-empty-guards prevent a failed
        // texture from materializing as a `""` reference in the .mat JSON,
        // which would silently bind a black texture at runtime.
        if (mat.pbrMetallicRoughness.baseColorTexture.index >= 0) {
            int tex_idx = model.textures[mat.pbrMetallicRoughness.baseColorTexture.index].source;
            if (tex_idx >= 0 && tex_idx < (int)cooked_textures.size() && !cooked_textures[tex_idx].empty()) {
                mat_file << "  \"albedo_texture\": \"" << cooked_textures[tex_idx] << "\",\n";
            }
        }
        if (mat.normalTexture.index >= 0) {
            int tex_idx = model.textures[mat.normalTexture.index].source;
            if (tex_idx >= 0 && tex_idx < (int)cooked_textures.size() && !cooked_textures[tex_idx].empty()) {
                mat_file << "  \"normal_texture\": \"" << cooked_textures[tex_idx] << "\",\n";
            }
        }

        if (mat.pbrMetallicRoughness.metallicRoughnessTexture.index >= 0) {
            int tex_idx = model.textures[mat.pbrMetallicRoughness.metallicRoughnessTexture.index].source;
            if (tex_idx >= 0 && tex_idx < (int)cooked_textures.size() && !cooked_textures[tex_idx].empty()) {
                mat_file << "  \"rma_texture\": \"" << cooked_textures[tex_idx] << "\",\n";
            }
        }
        mat_file << "  \"metallic\": " << mat.pbrMetallicRoughness.metallicFactor << ",\n";

        mat_file << "  \"roughness\": " << mat.pbrMetallicRoughness.roughnessFactor << "\n";
        mat_file << "}\n";
    }

    // Math helpers for glTF transforms
    struct Mat4 {
        float m[16];
        Mat4() {
            for (int i=0;i<16;i++) m[i] = (i%5==0)?1.0f:0.0f;
        }
        Mat4 operator*(const Mat4& r) const {
            Mat4 res;
            for (int i=0;i<4;i++) {
                for (int j=0;j<4;j++) {
                    res.m[i*4+j] = 0;
                    for (int k=0;k<4;k++) {
                        res.m[i*4+j] += m[i*4+k] * r.m[k*4+j];
                    }
                }
            }
            return res;
        }
        void transform(float& x, float& y, float& z) const {
            float nx = m[0]*x + m[1]*y + m[2]*z + m[3];
            float ny = m[4]*x + m[5]*y + m[6]*z + m[7];
            float nz = m[8]*x + m[9]*y + m[10]*z + m[11];
            float nw = m[12]*x + m[13]*y + m[14]*z + m[15];
            x = nx/nw; y = ny/nw; z = nz/nw;
        }
        void transform_normal(float& x, float& y, float& z) const {
            float nx = m[0]*x + m[1]*y + m[2]*z;
            float ny = m[4]*x + m[5]*y + m[6]*z;
            float nz = m[8]*x + m[9]*y + m[10]*z;
            x = nx; y = ny; z = nz;
            float l = std::sqrt(x*x+y*y+z*z);
            if(l > 0.0001f) { x/=l; y/=l; z/=l; }
        }
    };

    auto getNodeTransform = [](const tinygltf::Node& node) -> Mat4 {
        Mat4 mat;
        if (node.matrix.size() == 16) {
            for(int i=0;i<16;i++) mat.m[i] = (float)node.matrix[i];
            Mat4 row;
            for(int i=0;i<4;i++) for(int j=0;j<4;j++) row.m[i*4+j] = mat.m[j*4+i];
            return row;
        } else {
            Mat4 t, r, s;
            if (node.translation.size() == 3) {
                t.m[3] = (float)node.translation[0];
                t.m[7] = (float)node.translation[1];
                t.m[11] = (float)node.translation[2];
            }
            if (node.rotation.size() == 4) {
                float qx=(float)node.rotation[0], qy=(float)node.rotation[1], qz=(float)node.rotation[2], qw=(float)node.rotation[3];
                r.m[0] = 1.0f - 2.0f*qy*qy - 2.0f*qz*qz;
                r.m[1] = 2.0f*qx*qy - 2.0f*qz*qw;
                r.m[2] = 2.0f*qx*qz + 2.0f*qy*qw;
                r.m[4] = 2.0f*qx*qy + 2.0f*qz*qw;
                r.m[5] = 1.0f - 2.0f*qx*qx - 2.0f*qz*qz;
                r.m[6] = 2.0f*qy*qz - 2.0f*qx*qw;
                r.m[8] = 2.0f*qx*qz - 2.0f*qy*qw;
                r.m[9] = 2.0f*qy*qz + 2.0f*qx*qw;
                r.m[10] = 1.0f - 2.0f*qx*qx - 2.0f*qy*qy;
            }
            if (node.scale.size() == 3) {
                s.m[0] = (float)node.scale[0];
                s.m[5] = (float)node.scale[1];
                s.m[10] = (float)node.scale[2];
            }
            return t * r * s;
        }
    };

    const tinygltf::Scene& scene = model.scenes[defaultScene];

    fs::create_directories(fs::path(out_dir) / "scenes");
    std::string scene_path = (fs::path(out_dir) / "scenes" / (base_name + ".scene.json")).string();
    std::ofstream scene_file(scene_path);
    scene_file << "{\n  \"version\": 1,\n";
    scene_file << "  \"passes\": [\n";
    scene_file << "    {\n";
    scene_file << "      \"name\": \"PbrPass\",\n";
    scene_file << "      \"shader_vs\": \"shaders/pbr.slang\",\n";
    scene_file << "      \"shader_fs\": \"shaders/pbr.slang\",\n";
    scene_file << "      \"entry\": \"main\",\n";
    scene_file << "      \"clear_color\": [0.05, 0.06, 0.09, 1.0],\n";
    scene_file << "      \"draws\": []\n";
    scene_file << "    }\n";
    scene_file << "  ],\n";
    scene_file << "  \"models\": [\n";

    bool first_entity = true;

    for (int root_idx : scene.nodes) {
        const tinygltf::Node& root_node = model.nodes[root_idx];
        std::string obj_name = root_node.name.empty() ? (base_name + "_node_" + std::to_string(root_idx)) : root_node.name;
        std::replace(obj_name.begin(), obj_name.end(), ' ', '_');

        struct ExtractedPrimitive {
            std::vector<Vertex> v;
            std::vector<uint32_t> i;
            float min_x, min_y, min_z, max_x, max_y, max_z;
            int material_idx;
        };
        std::vector<ExtractedPrimitive> extracted;

        float total_min_x = std::numeric_limits<float>::max();
        float total_min_y = std::numeric_limits<float>::max();
        float total_min_z = std::numeric_limits<float>::max();
        float total_max_x = std::numeric_limits<float>::lowest();
        float total_max_y = std::numeric_limits<float>::lowest();
        float total_max_z = std::numeric_limits<float>::lowest();

        std::function<void(int, Mat4)> traverse = [&](int node_idx, Mat4 parent_mat) {
            const tinygltf::Node& node = model.nodes[node_idx];
            Mat4 local_mat = getNodeTransform(node);
            Mat4 world_mat = parent_mat * local_mat;

            if (node.mesh >= 0) {
                const tinygltf::Mesh& mesh = model.meshes[node.mesh];
                for (const auto& primitive : mesh.primitives) {
                    ExtractedPrimitive pdata;
                    pdata.min_x = std::numeric_limits<float>::max();
                    pdata.min_y = std::numeric_limits<float>::max();
                    pdata.min_z = std::numeric_limits<float>::max();
                    pdata.max_x = std::numeric_limits<float>::lowest();
                    pdata.max_y = std::numeric_limits<float>::lowest();
                    pdata.max_z = std::numeric_limits<float>::lowest();
                    pdata.material_idx = primitive.material;

                    const tinygltf::Accessor& posAccessor = model.accessors[primitive.attributes.at("POSITION")];
                    const tinygltf::BufferView& posView = model.bufferViews[posAccessor.bufferView];
                    const tinygltf::Buffer& posBuffer = model.buffers[posView.buffer];
                    const float* positions = reinterpret_cast<const float*>(&posBuffer.data[posView.byteOffset + posAccessor.byteOffset]);

                    const float* normals = nullptr;
                    if (primitive.attributes.count("NORMAL") > 0) {
                        const tinygltf::Accessor& normAccessor = model.accessors[primitive.attributes.at("NORMAL")];
                        const tinygltf::BufferView& normView = model.bufferViews[normAccessor.bufferView];
                        const tinygltf::Buffer& normBuffer = model.buffers[normView.buffer];
                        normals = reinterpret_cast<const float*>(&normBuffer.data[normView.byteOffset + normAccessor.byteOffset]);
                    }

                    const float* texcoords = nullptr;
                    if (primitive.attributes.count("TEXCOORD_0") > 0) {
                        const tinygltf::Accessor& uvAccessor = model.accessors[primitive.attributes.at("TEXCOORD_0")];
                        const tinygltf::BufferView& uvView = model.bufferViews[uvAccessor.bufferView];
                        const tinygltf::Buffer& uvBuffer = model.buffers[uvView.buffer];
                        texcoords = reinterpret_cast<const float*>(&uvBuffer.data[uvView.byteOffset + uvAccessor.byteOffset]);
                    }

                    const float* tangents = nullptr;
                    if (primitive.attributes.count("TANGENT") > 0) {
                        const tinygltf::Accessor& tanAccessor = model.accessors[primitive.attributes.at("TANGENT")];
                        const tinygltf::BufferView& tanView = model.bufferViews[tanAccessor.bufferView];
                        const tinygltf::Buffer& tanBuffer = model.buffers[tanView.buffer];
                        tangents = reinterpret_cast<const float*>(&tanBuffer.data[tanView.byteOffset + tanAccessor.byteOffset]);
                    }

                    for (size_t i = 0; i < posAccessor.count; ++i) {
                        Vertex v{};
                        v.px = positions[i * 3 + 0];
                        v.py = positions[i * 3 + 1];
                        v.pz = positions[i * 3 + 2];
                        world_mat.transform(v.px, v.py, v.pz);
                        
                        pdata.min_x = std::min(pdata.min_x, v.px);
                        pdata.min_y = std::min(pdata.min_y, v.py);
                        pdata.min_z = std::min(pdata.min_z, v.pz);
                        pdata.max_x = std::max(pdata.max_x, v.px);
                        pdata.max_y = std::max(pdata.max_y, v.py);
                        pdata.max_z = std::max(pdata.max_z, v.pz);

                        if (normals) {
                            v.nx = normals[i * 3 + 0];
                            v.ny = normals[i * 3 + 1];
                            v.nz = normals[i * 3 + 2];
                            world_mat.transform_normal(v.nx, v.ny, v.nz);
                        } else {
                            v.nx = 0.0f; v.ny = 1.0f; v.nz = 0.0f;
                        }

                        if (texcoords) {
                            v.tu = texcoords[i * 2 + 0];
                            v.tv = texcoords[i * 2 + 1];
                        } else {
                            v.tu = 0.0f; v.tv = 0.0f;
                        }

                        if (tangents) {
                            v.tx = tangents[i * 4 + 0];
                            v.ty = tangents[i * 4 + 1];
                            v.tz = tangents[i * 4 + 2];
                            v.tw = tangents[i * 4 + 3];
                            world_mat.transform_normal(v.tx, v.ty, v.tz); // Roughly valid
                        } else {
                            v.tx = 1.0f; v.ty = 0.0f; v.tz = 0.0f; v.tw = 1.0f;
                        }

                        pdata.v.push_back(v);
                    }

                    if (primitive.indices >= 0) {
                        const tinygltf::Accessor& indAccessor = model.accessors[primitive.indices];
                        const tinygltf::BufferView& indView = model.bufferViews[indAccessor.bufferView];
                        const tinygltf::Buffer& indBuffer = model.buffers[indView.buffer];
                        
                        if (indAccessor.componentType == TINYGLTF_COMPONENT_TYPE_UNSIGNED_SHORT) {
                            const uint16_t* ind = reinterpret_cast<const uint16_t*>(&indBuffer.data[indView.byteOffset + indAccessor.byteOffset]);
                            for (size_t i = 0; i < indAccessor.count; ++i) pdata.i.push_back((uint32_t)ind[i]);
                        } else if (indAccessor.componentType == TINYGLTF_COMPONENT_TYPE_UNSIGNED_INT) {
                            const uint32_t* ind = reinterpret_cast<const uint32_t*>(&indBuffer.data[indView.byteOffset + indAccessor.byteOffset]);
                            for (size_t i = 0; i < indAccessor.count; ++i) pdata.i.push_back(ind[i]);
                        }
                    }

                    total_min_x = std::min(total_min_x, pdata.min_x);
                    total_min_y = std::min(total_min_y, pdata.min_y);
                    total_min_z = std::min(total_min_z, pdata.min_z);
                    total_max_x = std::max(total_max_x, pdata.max_x);
                    total_max_y = std::max(total_max_y, pdata.max_y);
                    total_max_z = std::max(total_max_z, pdata.max_z);

                    extracted.push_back(pdata);
                }
            }

            for (int child_idx : node.children) {
                traverse(child_idx, world_mat);
            }
        };

        Mat4 identity;
        if (scale_x != 1.0f || scale_y != 1.0f || scale_z != 1.0f) {
            identity.m[0] = scale_x;
            identity.m[5] = scale_y;
            identity.m[10] = scale_z;
        }
        traverse(root_idx, identity);
        
        bool flip_winding = (scale_x * scale_y * scale_z) < 0.0f;

        if (extracted.empty()) continue;

        float pivot_x = (total_min_x + total_max_x) * 0.5f;
        float pivot_y = total_min_y;
        float pivot_z = (total_min_z + total_max_z) * 0.5f;

        std::atomic<size_t> total_indices{0};
        
        std::mutex mesh_job_mutex;
        size_t current_part = 0;
        std::vector<std::future<void>> mesh_futures;
        
        set_progress_stage("Importing Mesh: " + obj_name, extracted.size());
        
        for (unsigned int t = 0; t < num_threads; ++t) {
            mesh_futures.push_back(std::async(std::launch::async, [&]() {
                while (true) {
                    size_t i;
                    {
                        std::lock_guard<std::mutex> lock(mesh_job_mutex);
                        if (current_part >= extracted.size()) return;
                        i = current_part++;
                    }
                    auto& p = extracted[i];
                    
                    for (auto& v : p.v) {
                        v.px -= pivot_x;
                        v.py -= pivot_y;
                        v.pz -= pivot_z;
                    }
                    p.min_x -= pivot_x; p.min_y -= pivot_y; p.min_z -= pivot_z;
                    p.max_x -= pivot_x; p.max_y -= pivot_y; p.max_z -= pivot_z;

                    std::string hash_suffix = "";
                    {
                        uint64_t h = 14695981039346656037ULL;
                        auto fnv = [&](const void* data, size_t size) {
                            const uint8_t* ptr = reinterpret_cast<const uint8_t*>(data);
                            for (size_t k = 0; k < size; ++k) {
                                h ^= ptr[k];
                                h *= 1099511628211ULL;
                            }
                        };
                        fnv(obj_name.data(), obj_name.size());
                        if (!p.v.empty()) fnv(p.v.data(), p.v.size() * sizeof(Vertex));
                        if (!p.i.empty()) fnv(p.i.data(), p.i.size() * sizeof(uint32_t));
                        char buf[17];
                        snprintf(buf, sizeof(buf), "%016llx", (unsigned long long)h);
                        hash_suffix = std::string(buf).substr(0, 8);
                    }

                    std::string msh_name = obj_name + "_" + hash_suffix + "_part_" + std::to_string(i) + ".msh";
                    std::string msh_path = (fs::path(out_dir) / "models" / msh_name).string();
                    
                    std::ofstream out_file(msh_path, std::ios::binary);
                    if (out_file) {
                        uint32_t magic = 0x3148534D;
                        uint32_t v_count = (uint32_t)p.v.size();
                        uint32_t i_count = (uint32_t)p.i.size();
                        uint32_t index_format = 32;
                        out_file.write((char*)&magic, 4);
                        out_file.write((char*)&v_count, 4);
                        out_file.write((char*)&i_count, 4);
                        out_file.write((char*)&index_format, 4);
                        if (flip_winding) {
                            for (size_t j = 0; j < p.i.size(); j += 3) {
                                std::swap(p.i[j+1], p.i[j+2]);
                            }
                        }
                        
                        out_file.write((char*)p.v.data(), v_count * sizeof(Vertex));
                        out_file.write((char*)p.i.data(), i_count * 4);
                    }
                    total_indices.fetch_add(p.i.size(), std::memory_order_relaxed);
                    report_progress();
                }
            }));
        }
        
        for (auto& fut : mesh_futures) {
            fut.get();
        }

        std::string output_mdl = (fs::path(out_dir) / "models" / (obj_name + ".mdl")).string();
        std::ofstream mdl_file(output_mdl);
        mdl_file << "{\n  \"version\": 2,\n  \"parts\": [\n";

        for (size_t i = 0; i < extracted.size(); ++i) {
            auto& p = extracted[i];
            uint64_t h = 14695981039346656037ULL;
            auto fnv = [&](const void* data, size_t size) {
                const uint8_t* ptr = reinterpret_cast<const uint8_t*>(data);
                for (size_t k = 0; k < size; ++k) {
                    h ^= ptr[k];
                    h *= 1099511628211ULL;
                }
            };
            fnv(obj_name.data(), obj_name.size());
            if (!p.v.empty()) fnv(p.v.data(), p.v.size() * sizeof(Vertex));
            if (!p.i.empty()) fnv(p.i.data(), p.i.size() * sizeof(uint32_t));
            char buf[17];
            snprintf(buf, sizeof(buf), "%016llx", (unsigned long long)h);
            std::string hash_suffix = std::string(buf).substr(0, 8);

            std::string msh_name = obj_name + "_" + hash_suffix + "_part_" + std::to_string(i) + ".msh";

            
            mdl_file << "    { \"mesh\": \"" << msh_name << "\"";
            if (p.material_idx >= 0 && p.material_idx < cooked_materials.size()) {
                mdl_file << ", \"material\": \"" << cooked_materials[p.material_idx] << "\"";
            }
            mdl_file << ", \"bounds\": {\"min\": [" << p.min_x << ", " << p.min_y << ", " << p.min_z << "], \"max\": [" << p.max_x << ", " << p.max_y << ", " << p.max_z << "]}";
            mdl_file << " }" << (i == extracted.size() - 1 ? "" : ",") << "\n";
        }

        mdl_file << "  ],\n  \"bounds\": {\n";
        mdl_file << "    \"min\": [" << (total_min_x - pivot_x) << ", " << (total_min_y - pivot_y) << ", " << (total_min_z - pivot_z) << "],\n";
        mdl_file << "    \"max\": [" << (total_max_x - pivot_x) << ", " << (total_max_y - pivot_y) << ", " << (total_max_z - pivot_z) << "]\n";
        mdl_file << "  },\n  \"triangle_count\": " << (total_indices.load(std::memory_order_relaxed) / 3) << "\n}\n";

        if (!first_entity) scene_file << ",\n";
        first_entity = false;
        scene_file << "    {\n";
        scene_file << "      \"name\": \"" << obj_name << "\",\n";
        scene_file << "      \"source\": \"" << "models/" << obj_name + ".mdl" << "\",\n";
        scene_file << "      \"position\": [" << pivot_x << ", " << pivot_y << ", " << pivot_z << "],\n";
        scene_file << "      \"rotation\": [0, 0, 0, 1],\n";
        scene_file << "      \"scale\": [1, 1, 1]\n";
        scene_file << "    }\n";
        
        std::cout << "Cooked object: " << obj_name << " (" << extracted.size() << " parts)\n";
    }

    scene_file << "  ]\n}\n";
    scene_file.close();

    return 0;
}
