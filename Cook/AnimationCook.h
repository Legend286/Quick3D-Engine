/* SPDX-License-Identifier: MIT */
#pragma once

#include "tiny_gltf.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <limits>
#include <map>
#include <set>
#include <sstream>
#include <string>
#include <vector>

namespace animation_cook {
namespace fs = std::filesystem;

struct AnimationInfo {
    std::string name;
    double duration_seconds = 0.0;
    int channel_count = 0;
};

struct Inspection {
    bool has_mesh = false;
    bool has_skeleton = false;
    std::vector<AnimationInfo> animations;
};

struct Transform {
    double tx = 0.0;
    double ty = 0.0;
    double tz = 0.0;
    double qx = 0.0;
    double qy = 0.0;
    double qz = 0.0;
    double qw = 1.0;
    double sx = 1.0;
    double sy = 1.0;
    double sz = 1.0;
};

struct SkeletonData {
    int skin_index = -1;
    std::vector<int> joint_nodes;
    std::vector<int> parent_indices;
    std::vector<Transform> reference_pose;
    std::vector<std::array<double, 16>> inverse_bind;
    int root_bone = 0;
};

inline std::string JsonEscape(const std::string& value) {
    std::ostringstream out;
    for (char c : value) {
        switch (c) {
        case '\\': out << "\\\\"; break;
        case '"': out << "\\\""; break;
        case '\n': out << "\\n"; break;
        case '\r': out << "\\r"; break;
        case '\t': out << "\\t"; break;
        default: out << c; break;
        }
    }
    return out.str();
}

inline int ComponentCount(int type) {
    switch (type) {
    case TINYGLTF_TYPE_SCALAR: return 1;
    case TINYGLTF_TYPE_VEC2: return 2;
    case TINYGLTF_TYPE_VEC3: return 3;
    case TINYGLTF_TYPE_VEC4: return 4;
    case TINYGLTF_TYPE_MAT2: return 4;
    case TINYGLTF_TYPE_MAT3: return 9;
    case TINYGLTF_TYPE_MAT4: return 16;
    default: return 0;
    }
}

inline double ReadComponent(
    const unsigned char* data,
    int component_type,
    bool normalized) {
    switch (component_type) {
    case TINYGLTF_COMPONENT_TYPE_BYTE: {
        auto value = *reinterpret_cast<const int8_t*>(data);
        return normalized ? std::max(-1.0, value / 127.0) : value;
    }
    case TINYGLTF_COMPONENT_TYPE_UNSIGNED_BYTE: {
        auto value = *reinterpret_cast<const uint8_t*>(data);
        return normalized ? value / 255.0 : value;
    }
    case TINYGLTF_COMPONENT_TYPE_SHORT: {
        auto value = *reinterpret_cast<const int16_t*>(data);
        return normalized ? std::max(-1.0, value / 32767.0) : value;
    }
    case TINYGLTF_COMPONENT_TYPE_UNSIGNED_SHORT: {
        auto value = *reinterpret_cast<const uint16_t*>(data);
        return normalized ? value / 65535.0 : value;
    }
    case TINYGLTF_COMPONENT_TYPE_INT:
        return *reinterpret_cast<const int32_t*>(data);
    case TINYGLTF_COMPONENT_TYPE_UNSIGNED_INT:
        return *reinterpret_cast<const uint32_t*>(data);
    case TINYGLTF_COMPONENT_TYPE_FLOAT:
        return *reinterpret_cast<const float*>(data);
    case TINYGLTF_COMPONENT_TYPE_DOUBLE:
        return *reinterpret_cast<const double*>(data);
    default:
        return 0.0;
    }
}

inline std::vector<double> ReadAccessor(
    const tinygltf::Model& model,
    int accessor_index) {
    if (accessor_index < 0 || accessor_index >= static_cast<int>(model.accessors.size()))
        return {};
    const tinygltf::Accessor& accessor = model.accessors[accessor_index];
    if (accessor.bufferView < 0 ||
        accessor.bufferView >= static_cast<int>(model.bufferViews.size()))
        return {};
    const tinygltf::BufferView& view = model.bufferViews[accessor.bufferView];
    if (view.buffer < 0 || view.buffer >= static_cast<int>(model.buffers.size()))
        return {};
    const tinygltf::Buffer& buffer = model.buffers[view.buffer];
    int component_count = ComponentCount(accessor.type);
    int component_size = tinygltf::GetComponentSizeInBytes(
        static_cast<uint32_t>(accessor.componentType));
    if (component_count <= 0 || component_size <= 0)
        return {};
    int stride = accessor.ByteStride(view);
    if (stride <= 0)
        stride = component_count * component_size;

    size_t start = view.byteOffset + accessor.byteOffset;
    size_t required = start +
        (accessor.count == 0 ? 0 : (accessor.count - 1) * stride) +
        component_count * component_size;
    if (required > buffer.data.size())
        return {};

    std::vector<double> values;
    values.reserve(accessor.count * component_count);
    for (size_t index = 0; index < accessor.count; ++index) {
        const unsigned char* element = buffer.data.data() + start + index * stride;
        for (int component = 0; component < component_count; ++component) {
            values.push_back(ReadComponent(
                element + component * component_size,
                accessor.componentType,
                accessor.normalized));
        }
    }
    return values;
}

inline Transform NodeTransform(const tinygltf::Node& node) {
    Transform result;
    if (node.matrix.size() == 16) {
        result.tx = node.matrix[12];
        result.ty = node.matrix[13];
        result.tz = node.matrix[14];
        result.sx = std::sqrt(node.matrix[0] * node.matrix[0] +
                              node.matrix[1] * node.matrix[1] +
                              node.matrix[2] * node.matrix[2]);
        result.sy = std::sqrt(node.matrix[4] * node.matrix[4] +
                              node.matrix[5] * node.matrix[5] +
                              node.matrix[6] * node.matrix[6]);
        result.sz = std::sqrt(node.matrix[8] * node.matrix[8] +
                              node.matrix[9] * node.matrix[9] +
                              node.matrix[10] * node.matrix[10]);
        double trace = node.matrix[0] + node.matrix[5] + node.matrix[10];
        if (trace > 0.0) {
            double s = 0.5 / std::sqrt(trace + 1.0);
            result.qw = 0.25 / s;
            result.qx = (node.matrix[6] - node.matrix[9]) * s;
            result.qy = (node.matrix[8] - node.matrix[2]) * s;
            result.qz = (node.matrix[1] - node.matrix[4]) * s;
        }
        return result;
    }
    if (node.translation.size() == 3) {
        result.tx = node.translation[0];
        result.ty = node.translation[1];
        result.tz = node.translation[2];
    }
    if (node.rotation.size() == 4) {
        result.qx = node.rotation[0];
        result.qy = node.rotation[1];
        result.qz = node.rotation[2];
        result.qw = node.rotation[3];
    }
    if (node.scale.size() == 3) {
        result.sx = node.scale[0];
        result.sy = node.scale[1];
        result.sz = node.scale[2];
    }
    return result;
}

inline std::array<double, 16> IdentityMatrix() {
    std::array<double, 16> matrix{};
    matrix[0] = matrix[5] = matrix[10] = matrix[15] = 1.0;
    return matrix;
}

inline std::array<double, 16> ReadMatrix(
    const std::vector<double>& values,
    size_t offset) {
    std::array<double, 16> matrix = IdentityMatrix();
    if (offset + 16 > values.size())
        return matrix;
    for (int row = 0; row < 4; ++row) {
        for (int column = 0; column < 4; ++column) {
            matrix[row * 4 + column] = values[offset + column * 4 + row];
        }
    }
    return matrix;
}

inline Inspection Inspect(const tinygltf::Model& model) {
    Inspection result;
    for (const tinygltf::Mesh& mesh : model.meshes)
        result.has_mesh |= !mesh.primitives.empty();
    for (const tinygltf::Skin& skin : model.skins)
        result.has_skeleton |= !skin.joints.empty();
    for (size_t index = 0; index < model.animations.size(); ++index) {
        const tinygltf::Animation& animation = model.animations[index];
        double duration = 0.0;
        for (const tinygltf::AnimationSampler& sampler : animation.samplers) {
            for (double time : ReadAccessor(model, sampler.input))
                duration = std::max(duration, time);
        }
        result.animations.push_back({
            animation.name.empty()
                ? "Animation_" + std::to_string(index)
                : animation.name,
            duration,
            static_cast<int>(animation.channels.size()),
        });
    }
    return result;
}

inline void WriteInspectionJson(
    const Inspection& inspection,
    std::ostream& out) {
    out << "{\n"
        << "  \"has_mesh\": " << (inspection.has_mesh ? "true" : "false") << ",\n"
        << "  \"has_skeleton\": " << (inspection.has_skeleton ? "true" : "false") << ",\n"
        << "  \"animations\": [";
    for (size_t index = 0; index < inspection.animations.size(); ++index) {
        const AnimationInfo& animation = inspection.animations[index];
        if (index != 0) out << ",";
        out << "\n    { \"name\": \"" << JsonEscape(animation.name)
            << "\", \"duration\": " << std::setprecision(9)
            << animation.duration_seconds
            << ", \"channels\": " << animation.channel_count << " }";
    }
    out << "\n  ]\n}\n";
}

inline SkeletonData BuildSkeleton(
    const tinygltf::Model& model,
    double scale_x = 1.0,
    double scale_y = 1.0,
    double scale_z = 1.0) {
    SkeletonData result;
    for (size_t skin_index = 0; skin_index < model.skins.size(); ++skin_index) {
        if (!model.skins[skin_index].joints.empty()) {
            result.skin_index = static_cast<int>(skin_index);
            result.joint_nodes = model.skins[skin_index].joints;
            break;
        }
    }
    if (result.skin_index < 0)
        return result;

    std::map<int, int> node_to_bone;
    for (size_t index = 0; index < result.joint_nodes.size(); ++index) {
        int node_index = result.joint_nodes[index];
        if (node_index < 0 || node_index >= static_cast<int>(model.nodes.size()))
            return {};
        node_to_bone[node_index] = static_cast<int>(index);
    }
    result.parent_indices.assign(result.joint_nodes.size(), -1);
    result.reference_pose.reserve(result.joint_nodes.size());
    for (size_t index = 0; index < result.joint_nodes.size(); ++index) {
        int node_index = result.joint_nodes[index];
        if (node_index < 0 || node_index >= static_cast<int>(model.nodes.size()))
            continue;
        const tinygltf::Node& node = model.nodes[node_index];
        result.reference_pose.push_back(NodeTransform(node));
        for (size_t candidate = 0; candidate < result.joint_nodes.size(); ++candidate) {
            const tinygltf::Node& parent = model.nodes[result.joint_nodes[candidate]];
            if (std::find(parent.children.begin(), parent.children.end(), node_index) != parent.children.end()) {
                result.parent_indices[index] = static_cast<int>(candidate);
                break;
            }
        }
    }
    if (result.reference_pose.size() < result.joint_nodes.size())
        result.reference_pose.resize(result.joint_nodes.size());

    const tinygltf::Skin& skin = model.skins[result.skin_index];
    if (skin.skeleton >= 0 && node_to_bone.count(skin.skeleton) != 0)
        result.root_bone = node_to_bone[skin.skeleton];
    else {
        result.root_bone = 0;
        for (size_t index = 0; index < result.parent_indices.size(); ++index) {
            if (result.parent_indices[index] < 0) {
                result.root_bone = static_cast<int>(index);
                break;
            }
        }
    }

    result.inverse_bind.assign(result.joint_nodes.size(), IdentityMatrix());
    if (skin.inverseBindMatrices >= 0) {
        std::vector<double> values = ReadAccessor(model, skin.inverseBindMatrices);
        for (size_t index = 0; index < result.inverse_bind.size(); ++index)
            result.inverse_bind[index] = ReadMatrix(values, index * 16);
    }
    if (scale_x != 1.0 || scale_y != 1.0 || scale_z != 1.0) {
        for (Transform& pose : result.reference_pose) {
            pose.tx *= scale_x;
            pose.ty *= scale_y;
            pose.tz *= scale_z;
        }
        // Conjugate inverse-bind by the scale so bind-pose skinning stays
        // identity: skin = global' * inverseBind' with global' = S*global*S^-1
        // and inverseBind' = S*inverseBind*S^-1 telescopes to S*S^-1 = I.
        const double safe_scale_x = scale_x == 0.0 ? 1.0 : scale_x;
        const double safe_scale_y = scale_y == 0.0 ? 1.0 : scale_y;
        const double safe_scale_z = scale_z == 0.0 ? 1.0 : scale_z;
        const double row_scale[4] = {
            safe_scale_x, safe_scale_y, safe_scale_z, 1.0 };
        const double col_scale[4] = {
            1.0 / safe_scale_x, 1.0 / safe_scale_y,
            1.0 / safe_scale_z, 1.0 };
        for (auto& matrix : result.inverse_bind) {
            for (int row = 0; row < 4; ++row) {
                for (int column = 0; column < 4; ++column) {
                    matrix[row * 4 + column] *=
                        row_scale[row] * col_scale[column];
                }
            }
        }
    }
    return result;
}

inline void WriteTransformFields(std::ostream& out, const Transform& transform) {
    out << "\"translation\": [" << transform.tx << ", " << transform.ty << ", " << transform.tz
        << "], \"rotation\": [" << transform.qx << ", " << transform.qy << ", " << transform.qz << ", " << transform.qw
        << "], \"scale\": [" << transform.sx << ", " << transform.sy << ", " << transform.sz << "]";
}

inline void WriteTransform(std::ostream& out, const Transform& transform) {
    out << "{ ";
    WriteTransformFields(out, transform);
    out << " }";
}

inline void WriteSkeleton(
    const SkeletonData& skeleton,
    const tinygltf::Model& model,
    const fs::path& path) {
    std::ofstream out(path);
    out << "{\n  \"version\": 1,\n  \"root_bone\": " << skeleton.root_bone << ",\n  \"bones\": [\n";
    for (size_t index = 0; index < skeleton.joint_nodes.size(); ++index) {
        int node_index = skeleton.joint_nodes[index];
        std::string name = node_index >= 0 && node_index < static_cast<int>(model.nodes.size())
            ? model.nodes[node_index].name
            : "";
        if (name.empty()) name = "bone_" + std::to_string(index);
        out << "    { \"name\": \"" << JsonEscape(name)
            << "\", \"parent\": " << skeleton.parent_indices[index] << ", ";
        WriteTransformFields(out, skeleton.reference_pose[index]);
        out << " }" << (index + 1 == skeleton.joint_nodes.size() ? "" : ",") << "\n";
    }
    out << "  ],\n  \"inverse_bind_matrices\": [\n";
    for (size_t index = 0; index < skeleton.inverse_bind.size(); ++index) {
        out << "    [";
        for (size_t value = 0; value < 16; ++value) {
            if (value != 0) out << ", ";
            out << skeleton.inverse_bind[index][value];
        }
        out << "]" << (index + 1 == skeleton.inverse_bind.size() ? "" : ",") << "\n";
    }
    out << "  ]\n}\n";
}

inline Transform Interpolate(
    const Transform& a,
    const Transform& b,
    double alpha) {
    Transform result = a;
    auto lerp = [alpha](double x, double y) { return x + (y - x) * alpha; };
    result.tx = lerp(a.tx, b.tx);
    result.ty = lerp(a.ty, b.ty);
    result.tz = lerp(a.tz, b.tz);
    result.sx = lerp(a.sx, b.sx);
    result.sy = lerp(a.sy, b.sy);
    result.sz = lerp(a.sz, b.sz);
    double bq_x = b.qx, bq_y = b.qy, bq_z = b.qz, bq_w = b.qw;
    double dot = a.qx * bq_x + a.qy * bq_y + a.qz * bq_z + a.qw * bq_w;
    if (dot < 0.0) {
        bq_x = -bq_x;
        bq_y = -bq_y;
        bq_z = -bq_z;
        bq_w = -bq_w;
    }
    result.qx = lerp(a.qx, bq_x);
    result.qy = lerp(a.qy, bq_y);
    result.qz = lerp(a.qz, bq_z);
    result.qw = lerp(a.qw, bq_w);
    double length = std::sqrt(result.qx * result.qx + result.qy * result.qy + result.qz * result.qz + result.qw * result.qw);
    if (length > 1e-8) {
        result.qx /= length;
        result.qy /= length;
        result.qz /= length;
        result.qw /= length;
    }
    return result;
}

inline Transform SampleChannel(
    const tinygltf::Model& model,
    const tinygltf::AnimationSampler& sampler,
    const std::string& target_path,
    double time,
    const Transform& base,
    double scale_x = 1.0,
    double scale_y = 1.0,
    double scale_z = 1.0) {
    std::vector<double> times = ReadAccessor(model, sampler.input);
    std::vector<double> values = ReadAccessor(model, sampler.output);
    int component_count = target_path == "rotation" ? 4 : 3;
    if (times.empty() || values.size() < component_count)
        return base;
    size_t key = 0;
    while (key + 1 < times.size() && times[key + 1] <= time)
        ++key;
    size_t value_stride = component_count;
    if (sampler.interpolation == "CUBICSPLINE")
        value_stride *= 3;
    size_t value_index = key * value_stride +
        (sampler.interpolation == "CUBICSPLINE" ? value_stride / 3 : 0);
    if (value_index + component_count > values.size())
        return base;
    Transform sampled = base;
    auto read = [&](int component) { return values[value_index + component]; };
    if (target_path == "translation") {
        sampled.tx = read(0) * scale_x;
        sampled.ty = read(1) * scale_y;
        sampled.tz = read(2) * scale_z;
    } else if (target_path == "scale") {
        sampled.sx = read(0); sampled.sy = read(1); sampled.sz = read(2);
    } else if (target_path == "rotation") {
        sampled.qx = read(0); sampled.qy = read(1); sampled.qz = read(2); sampled.qw = read(3);
    }
    if (key + 1 >= times.size() || sampler.interpolation == "STEP")
        return sampled;

    double span = times[key + 1] - times[key];
    if (span <= 1e-8)
        return sampled;
    double alpha = std::clamp((time - times[key]) / span, 0.0, 1.0);
    size_t next_index = (key + 1) * value_stride +
        (sampler.interpolation == "CUBICSPLINE" ? value_stride / 3 : 0);
    if (next_index + component_count > values.size())
        return sampled;
    Transform next = base;
    auto read_next = [&](int component) { return values[next_index + component]; };
    if (target_path == "translation") {
        next.tx = read_next(0) * scale_x;
        next.ty = read_next(1) * scale_y;
        next.tz = read_next(2) * scale_z;
    } else if (target_path == "scale") {
        next.sx = read_next(0); next.sy = read_next(1); next.sz = read_next(2);
    } else if (target_path == "rotation") {
        next.qx = read_next(0); next.qy = read_next(1); next.qz = read_next(2); next.qw = read_next(3);
    }
    return Interpolate(sampled, next, alpha);
}

inline bool WriteAnimation(
    const tinygltf::Model& model,
    const SkeletonData& skeleton,
    const std::vector<std::string>& selected_names,
    const fs::path& skeleton_path,
    const fs::path& animation_path,
    double scale_x = 1.0,
    double scale_y = 1.0,
    double scale_z = 1.0) {
    std::map<std::string, size_t> animation_indices;
    for (size_t index = 0; index < model.animations.size(); ++index) {
        std::string name = model.animations[index].name.empty()
            ? "Animation_" + std::to_string(index)
            : model.animations[index].name;
        animation_indices[name] = index;
    }

    std::ofstream out(animation_path);
    out << "{\n  \"version\": 1,\n  \"skeleton_path\": \""
        << JsonEscape(skeleton_path.filename().string()) << "\",\n  \"clips\": [\n";
    bool wrote_clip = false;
    for (const std::string& selected_name : selected_names) {
        auto found = animation_indices.find(selected_name);
        if (found == animation_indices.end())
            continue;
        const tinygltf::Animation& animation = model.animations[found->second];
        double duration = 0.0;
        for (const tinygltf::AnimationSampler& sampler : animation.samplers)
            for (double time : ReadAccessor(model, sampler.input))
                duration = std::max(duration, time);
        constexpr int sample_rate = 30;
        const bool looping = true;
        int frame_count = std::max(1, static_cast<int>(std::ceil(duration * sample_rate)) + 1);
        std::vector<std::vector<Transform>> frames(
            frame_count,
            skeleton.reference_pose);
        for (int frame = 0; frame < frame_count; ++frame) {
            double time = std::min(duration, frame / static_cast<double>(sample_rate));
            for (const tinygltf::AnimationChannel& channel : animation.channels) {
                if (channel.target_node < 0)
                    continue;
                auto joint = std::find(
                    skeleton.joint_nodes.begin(),
                    skeleton.joint_nodes.end(),
                    channel.target_node);
                if (joint == skeleton.joint_nodes.end() ||
                    channel.sampler < 0 ||
                    channel.sampler >= static_cast<int>(animation.samplers.size()))
                    continue;
                size_t bone_index = static_cast<size_t>(
                    joint - skeleton.joint_nodes.begin());
                frames[frame][bone_index] = SampleChannel(
                    model,
                    animation.samplers[channel.sampler],
                    channel.target_path,
                    time,
                    frames[frame][bone_index],
                    scale_x,
                    scale_y,
                    scale_z);
            }
        }
        // Looping clips must wrap back to the first pose exactly at the clip
        // end; otherwise the GPU's modulo wrap blends the last keyframe into
        // frame 0 and the loop visibly snaps back or holds.
        if (looping && frame_count > 1)
            frames.back() = frames.front();

        if (wrote_clip) out << ",\n";
        wrote_clip = true;
        out << "    { \"name\": \"" << JsonEscape(selected_name)
            << "\", \"sample_rate\": " << sample_rate
            << ", \"duration\": " << duration
            << ", \"looping\": " << (looping ? "true" : "false")
            << ", \"frames\": [\n";
        for (size_t frame = 0; frame < frames.size(); ++frame) {
            out << "      [";
            for (size_t bone = 0; bone < frames[frame].size(); ++bone) {
                if (bone != 0) out << ", ";
                WriteTransform(out, frames[frame][bone]);
            }
            out << "]" << (frame + 1 == frames.size() ? "" : ",") << "\n";
        }
        out << "    ] }";
    }
    out << "\n  ]\n}\n";
    return wrote_clip;
}

inline bool Cook(
    const tinygltf::Model& model,
    const fs::path& output_directory,
    const std::string& base_name,
    const std::vector<std::string>& selected_animations,
    bool import_skeleton,
    double scale_x = 1.0,
    double scale_y = 1.0,
    double scale_z = 1.0) {
    SkeletonData skeleton = BuildSkeleton(
        model, scale_x, scale_y, scale_z);
    if (skeleton.skin_index < 0)
        return false;
    fs::create_directories(output_directory);
    fs::path skeleton_path = output_directory / (base_name + ".skel");
    WriteSkeleton(skeleton, model, skeleton_path);
    if (import_skeleton && !selected_animations.empty()) {
        if (!WriteAnimation(
                model,
                skeleton,
                selected_animations,
                skeleton_path,
                output_directory / (base_name + ".anim"),
                scale_x,
                scale_y,
                scale_z)) {
            return false;
        }
    }
    return true;
}

} // namespace animation_cook
