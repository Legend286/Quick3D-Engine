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

std::string ExecuteBasisu(const std::string& input_img, const std::string& out_dir, bool is_normal, bool is_linear, int width, int height, int channels, const std::string& type_name) {
    std::string cmd = "./out/basisu -ktx2 \"" + input_img + "\" -output_path \"" + out_dir + "\"";
    if (is_normal) cmd += " -normal_map";
    if (is_linear) cmd += " -linear";
    std::cout << "Running: " << cmd << "\n";
    int ret = std::system(cmd.c_str());
    if (ret != 0) {
        std::cerr << "Warning: basisu failed for " << input_img << "\n";
    }
    fs::path p(input_img);
    std::string base_name = p.stem().string();
    std::string ktx2_path = (fs::path(out_dir) / base_name).string() + ".ktx2";
    
    // Write out .tex metadata
    std::string tex_meta_path = (fs::path(out_dir) / base_name).string() + ".tex";
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

    // Return the relative filename for serialization in materials
    return base_name + ".ktx2";
}

int main(int argc, char** argv) {
    if (argc < 2) {
        std::cerr << "Usage: engine_cook <input.glb/gltf> [out_dir]\n";
        return 1;
    }

    std::string input_file = argv[1];
    fs::path in_path(input_file);
    std::string out_dir = (argc >= 3) ? argv[2] : in_path.parent_path().string();
    if (out_dir.empty()) out_dir = ".";
    
    fs::create_directories(out_dir);

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
        return 1;
    }

    
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

    for (size_t i = 0; i < model.images.size(); ++i) {
        
        texture_futures.push_back(std::async(std::launch::async, [&, i]() {
            auto& img = model.images[i];
            std::string temp_png = (fs::path(out_dir) / (base_name + "_tex_" + std::to_string(i) + ".png")).string();
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
                    std::cerr << "Failed to load external texture " << ext_path << "\\n";
                    return;
                }
            } else {
                return;
            }
            
            bool is_normal = (tex_types[i] == TexType::Normal);
            bool is_linear = (tex_types[i] == TexType::Normal || tex_types[i] == TexType::RMA);
            std::string type_name = "albedo";
            if (tex_types[i] == TexType::Normal) type_name = "normal";
            if (tex_types[i] == TexType::RMA) type_name = "rma";
            
            std::string out_ktx2 = ExecuteBasisu(input_img, out_dir, is_normal, is_linear, w, h, comp, type_name);
            
            // Delete temp png if we extracted it
            if (input_img == temp_png) {
                fs::remove(temp_png);
            }
            
            cooked_textures[i] = out_ktx2;
        }));

    }

    for (auto& fut : texture_futures) {
        fut.get();
    }

    // 2. Process Materials
    std::vector<std::string> cooked_materials;
    for (size_t i = 0; i < model.materials.size(); ++i) {
        auto& mat = model.materials[i];
        std::string mat_name = mat.name.empty() ? (base_name + "_mat_" + std::to_string(i)) : mat.name;
        std::string output_mat = (fs::path(out_dir) / (mat_name + ".mat")).string();
        cooked_materials.push_back(mat_name + ".mat");

        std::ofstream mat_file(output_mat);
        mat_file << "{\n";
        mat_file << "  \"version\": 1,\n";
        mat_file << "  \"albedo_color\": [" 
                 << mat.pbrMetallicRoughness.baseColorFactor[0] << ", "
                 << mat.pbrMetallicRoughness.baseColorFactor[1] << ", "
                 << mat.pbrMetallicRoughness.baseColorFactor[2] << ", "
                 << mat.pbrMetallicRoughness.baseColorFactor[3] << "],\n";
                 
        if (mat.pbrMetallicRoughness.baseColorTexture.index >= 0) {
            int tex_idx = model.textures[mat.pbrMetallicRoughness.baseColorTexture.index].source;
            mat_file << "  \"albedo_texture\": \"" << cooked_textures[tex_idx] << "\",\n";
        }
        if (mat.normalTexture.index >= 0) {
            int tex_idx = model.textures[mat.normalTexture.index].source;
            mat_file << "  \"normal_texture\": \"" << cooked_textures[tex_idx] << "\",\n";
        }
        
        if (mat.pbrMetallicRoughness.metallicRoughnessTexture.index >= 0) {
            int tex_idx = model.textures[mat.pbrMetallicRoughness.metallicRoughnessTexture.index].source;
            mat_file << "  \"rma_texture\": \"" << cooked_textures[tex_idx] << "\",\n";
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

    int defaultScene = model.defaultScene > -1 ? model.defaultScene : 0;
    const tinygltf::Scene& scene = model.scenes[defaultScene];

    std::string scene_path = (fs::path(out_dir) / (base_name + ".scene.json")).string();
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
        traverse(root_idx, identity);

        if (extracted.empty()) continue;

        float pivot_x = (total_min_x + total_max_x) * 0.5f;
        float pivot_y = total_min_y;
        float pivot_z = (total_min_z + total_max_z) * 0.5f;

        size_t total_indices = 0;
        
        std::string output_mdl = (fs::path(out_dir) / (obj_name + ".mdl")).string();
        std::ofstream mdl_file(output_mdl);
        mdl_file << "{\n  \"version\": 2,\n  \"parts\": [\n";

        for (size_t i = 0; i < extracted.size(); ++i) {
            auto& p = extracted[i];
            
            for (auto& v : p.v) {
                v.px -= pivot_x;
                v.py -= pivot_y;
                v.pz -= pivot_z;
            }
            p.min_x -= pivot_x; p.min_y -= pivot_y; p.min_z -= pivot_z;
            p.max_x -= pivot_x; p.max_y -= pivot_y; p.max_z -= pivot_z;

            std::string msh_name = obj_name + "_part_" + std::to_string(i) + ".msh";
            std::string msh_path = (fs::path(out_dir) / msh_name).string();
            
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
                out_file.write((char*)p.v.data(), v_count * sizeof(Vertex));
                out_file.write((char*)p.i.data(), i_count * 4);
            }
            total_indices += p.i.size();

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
        mdl_file << "  },\n  \"triangle_count\": " << (total_indices / 3) << "\n}\n";

        if (!first_entity) scene_file << ",\n";
        first_entity = false;
        scene_file << "    {\n";
        scene_file << "      \"name\": \"" << obj_name << "\",\n";
        scene_file << "      \"source\": \"" << obj_name + ".mdl" << "\",\n";
        scene_file << "      \"position\": [" << pivot_x << ", " << pivot_y << ", " << pivot_z << "],\n";
        scene_file << "      \"rotation\": [0, 0, 0],\n";
        scene_file << "      \"scale\": [1, 1, 1]\n";
        scene_file << "    }\n";
        
        std::cout << "Cooked object: " << obj_name << " (" << extracted.size() << " parts)\n";
    }

    scene_file << "  ]\n}\n";
    scene_file.close();

    return 0;
}
