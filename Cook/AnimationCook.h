/* SPDX-License-Identifier: MIT */
#pragma once

#include "tiny_gltf.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <functional>
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
    if (!data)
        return 0.0;

    switch (component_type) {
    case TINYGLTF_COMPONENT_TYPE_BYTE: {
        int8_t value = 0;
        std::memcpy(&value, data, sizeof(value));
        return normalized ? std::max(-1.0, static_cast<double>(value) / 127.0)
                          : static_cast<double>(value);
    }
    case TINYGLTF_COMPONENT_TYPE_UNSIGNED_BYTE: {
        uint8_t value = 0;
        std::memcpy(&value, data, sizeof(value));
        return normalized ? static_cast<double>(value) / 255.0
                          : static_cast<double>(value);
    }
    case TINYGLTF_COMPONENT_TYPE_SHORT: {
        int16_t value = 0;
        std::memcpy(&value, data, sizeof(value));
        return normalized ? std::max(-1.0, static_cast<double>(value) / 32767.0)
                          : static_cast<double>(value);
    }
    case TINYGLTF_COMPONENT_TYPE_UNSIGNED_SHORT: {
        uint16_t value = 0;
        std::memcpy(&value, data, sizeof(value));
        return normalized ? static_cast<double>(value) / 65535.0
                          : static_cast<double>(value);
    }
    case TINYGLTF_COMPONENT_TYPE_INT: {
        int32_t value = 0;
        std::memcpy(&value, data, sizeof(value));
        return static_cast<double>(value);
    }
    case TINYGLTF_COMPONENT_TYPE_UNSIGNED_INT: {
        uint32_t value = 0;
        std::memcpy(&value, data, sizeof(value));
        return static_cast<double>(value);
    }
    case TINYGLTF_COMPONENT_TYPE_FLOAT: {
        float value = 0.0f;
        std::memcpy(&value, data, sizeof(value));
        return static_cast<double>(value);
    }
    case TINYGLTF_COMPONENT_TYPE_DOUBLE: {
        double value = 0.0;
        std::memcpy(&value, data, sizeof(value));
        return value;
    }
    default:
        return 0.0;
    }
}

inline std::vector<double> ReadAccessor(
    const tinygltf::Model& model,
    int accessor_index) {
    if (accessor_index < 0 ||
        accessor_index >= static_cast<int>(model.accessors.size()))
        return {};

    const tinygltf::Accessor& accessor = model.accessors[accessor_index];
    const int component_count = ComponentCount(accessor.type);
    const int component_size = tinygltf::GetComponentSizeInBytes(
        static_cast<uint32_t>(accessor.componentType));
    if (component_count <= 0 || component_size <= 0)
        return {};

    std::vector<double> values(
        accessor.count * static_cast<size_t>(component_count), 0.0);

    // A sparse accessor may legally omit its base buffer view. In that case
    // the base values are zero and the sparse values below overwrite them.
    if (accessor.bufferView >= 0) {
        if (accessor.bufferView >= static_cast<int>(model.bufferViews.size()))
            return {};
        const tinygltf::BufferView& view = model.bufferViews[accessor.bufferView];
        if (view.buffer < 0 || view.buffer >= static_cast<int>(model.buffers.size()))
            return {};
        const tinygltf::Buffer& buffer = model.buffers[view.buffer];
        int stride = accessor.ByteStride(view);
        if (stride <= 0)
            stride = component_count * component_size;

        const size_t start = view.byteOffset + accessor.byteOffset;
        const size_t required = start +
            (accessor.count == 0 ? 0 : (accessor.count - 1) * static_cast<size_t>(stride)) +
            static_cast<size_t>(component_count * component_size);
        if (required > buffer.data.size())
            return {};

        for (size_t index = 0; index < accessor.count; ++index) {
            const unsigned char* element =
                buffer.data.data() + start + index * static_cast<size_t>(stride);
            for (int component = 0; component < component_count; ++component) {
                values[index * component_count + component] = ReadComponent(
                    element + component * component_size,
                    accessor.componentType,
                    accessor.normalized);
            }
        }
    }

    if (accessor.sparse.isSparse && accessor.sparse.count > 0) {
        const auto& sparse = accessor.sparse;
        if (sparse.indices.bufferView < 0 ||
            sparse.indices.bufferView >= static_cast<int>(model.bufferViews.size()) ||
            sparse.values.bufferView < 0 ||
            sparse.values.bufferView >= static_cast<int>(model.bufferViews.size()))
            return {};

        const tinygltf::BufferView& index_view =
            model.bufferViews[sparse.indices.bufferView];
        const tinygltf::BufferView& value_view =
            model.bufferViews[sparse.values.bufferView];
        if (index_view.buffer < 0 ||
            index_view.buffer >= static_cast<int>(model.buffers.size()) ||
            value_view.buffer < 0 ||
            value_view.buffer >= static_cast<int>(model.buffers.size()))
            return {};

        const tinygltf::Buffer& index_buffer = model.buffers[index_view.buffer];
        const tinygltf::Buffer& value_buffer = model.buffers[value_view.buffer];
        const int index_size = tinygltf::GetComponentSizeInBytes(
            static_cast<uint32_t>(sparse.indices.componentType));
        if (index_size <= 0)
            return {};

        const size_t index_start = index_view.byteOffset + sparse.indices.byteOffset;
        const size_t value_start = value_view.byteOffset + sparse.values.byteOffset;
        const size_t sparse_value_stride =
            static_cast<size_t>(component_count * component_size);
        if (index_start + sparse.count * static_cast<size_t>(index_size) >
                index_buffer.data.size() ||
            value_start + sparse.count * sparse_value_stride >
                value_buffer.data.size())
            return {};

        for (size_t sparse_index = 0; sparse_index < static_cast<size_t>(sparse.count); ++sparse_index) {
            const unsigned char* index_ptr = index_buffer.data.data() +
                index_start + sparse_index * static_cast<size_t>(index_size);
            const double decoded_index = ReadComponent(
                index_ptr, sparse.indices.componentType, false);
            if (!std::isfinite(decoded_index) || decoded_index < 0.0 ||
                std::floor(decoded_index) != decoded_index ||
                decoded_index >= static_cast<double>(accessor.count))
                return {};

            const size_t destination = static_cast<size_t>(decoded_index);
            const unsigned char* element = value_buffer.data.data() +
                value_start + sparse_index * sparse_value_stride;
            for (int component = 0; component < component_count; ++component) {
                values[destination * component_count + component] = ReadComponent(
                    element + component * component_size,
                    accessor.componentType,
                    accessor.normalized);
            }
        }
    }

    return values;
}

using Matrix4 = std::array<double, 16>;

inline Matrix4 IdentityMatrix() {
    Matrix4 matrix{};
    matrix[0] = matrix[5] = matrix[10] = matrix[15] = 1.0;
    return matrix;
}

inline Matrix4 MultiplyMatrix(const Matrix4& a, const Matrix4& b) {
    Matrix4 result{};
    for (int row = 0; row < 4; ++row) {
        for (int column = 0; column < 4; ++column) {
            for (int k = 0; k < 4; ++k)
                result[row * 4 + column] +=
                    a[row * 4 + k] * b[k * 4 + column];
        }
    }
    return result;
}

inline Matrix4 TransposeMatrix(const Matrix4& matrix) {
    Matrix4 transposed{};
    for (int row = 0; row < 4; ++row)
        for (int column = 0; column < 4; ++column)
            transposed[row * 4 + column] = matrix[column * 4 + row];
    return transposed;
}

inline bool InvertMatrix(const Matrix4& source, Matrix4& inverse) {
    double augmented[4][8]{};
    for (int row = 0; row < 4; ++row) {
        for (int column = 0; column < 4; ++column)
            augmented[row][column] = source[row * 4 + column];
        augmented[row][row + 4] = 1.0;
    }

    for (int column = 0; column < 4; ++column) {
        int pivot = column;
        for (int row = column + 1; row < 4; ++row) {
            if (std::abs(augmented[row][column]) >
                std::abs(augmented[pivot][column]))
                pivot = row;
        }
        if (std::abs(augmented[pivot][column]) <= 1.0e-12)
            return false;
        if (pivot != column) {
            for (int item = 0; item < 8; ++item)
                std::swap(augmented[pivot][item], augmented[column][item]);
        }

        const double divisor = augmented[column][column];
        for (int item = 0; item < 8; ++item)
            augmented[column][item] /= divisor;

        for (int row = 0; row < 4; ++row) {
            if (row == column)
                continue;
            const double factor = augmented[row][column];
            for (int item = 0; item < 8; ++item)
                augmented[row][item] -= factor * augmented[column][item];
        }
    }

    for (int row = 0; row < 4; ++row)
        for (int column = 0; column < 4; ++column)
            inverse[row * 4 + column] = augmented[row][column + 4];
    return true;
}

inline Matrix4 ComposeMatrix(const Transform& transform) {
    double qx = transform.qx;
    double qy = transform.qy;
    double qz = transform.qz;
    double qw = transform.qw;
    const double quaternion_length =
        std::sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
    if (quaternion_length > 1.0e-12) {
        qx /= quaternion_length;
        qy /= quaternion_length;
        qz /= quaternion_length;
        qw /= quaternion_length;
    } else {
        qx = qy = qz = 0.0;
        qw = 1.0;
    }

    const double xx = qx * qx;
    const double yy = qy * qy;
    const double zz = qz * qz;
    const double xy = qx * qy;
    const double xz = qx * qz;
    const double yz = qy * qz;
    const double wx = qw * qx;
    const double wy = qw * qy;
    const double wz = qw * qz;

    Matrix4 matrix = IdentityMatrix();
    matrix[0] = (1.0 - 2.0 * (yy + zz)) * transform.sx;
    matrix[1] = (2.0 * (xy - wz)) * transform.sy;
    matrix[2] = (2.0 * (xz + wy)) * transform.sz;
    matrix[3] = transform.tx;
    matrix[4] = (2.0 * (xy + wz)) * transform.sx;
    matrix[5] = (1.0 - 2.0 * (xx + zz)) * transform.sy;
    matrix[6] = (2.0 * (yz - wx)) * transform.sz;
    matrix[7] = transform.ty;
    matrix[8] = (2.0 * (xz - wy)) * transform.sx;
    matrix[9] = (2.0 * (yz + wx)) * transform.sy;
    matrix[10] = (1.0 - 2.0 * (xx + yy)) * transform.sz;
    matrix[11] = transform.tz;
    return matrix;
}

inline Transform DecomposeMatrix(const Matrix4& matrix) {
    Transform result;
    result.tx = matrix[3];
    result.ty = matrix[7];
    result.tz = matrix[11];

    double c0[3] = { matrix[0], matrix[4], matrix[8] };
    double c1[3] = { matrix[1], matrix[5], matrix[9] };
    double c2[3] = { matrix[2], matrix[6], matrix[10] };
    auto length3 = [](const double value[3]) {
        return std::sqrt(value[0] * value[0] +
                         value[1] * value[1] +
                         value[2] * value[2]);
    };
    result.sx = length3(c0);
    result.sy = length3(c1);
    result.sz = length3(c2);

    const double determinant =
        c0[0] * (c1[1] * c2[2] - c1[2] * c2[1]) -
        c1[0] * (c0[1] * c2[2] - c0[2] * c2[1]) +
        c2[0] * (c0[1] * c1[2] - c0[2] * c1[1]);
    if (determinant < 0.0)
        result.sx = -result.sx;

    const double safe_x = std::abs(result.sx) > 1.0e-12 ? result.sx : 1.0;
    const double safe_y = std::abs(result.sy) > 1.0e-12 ? result.sy : 1.0;
    const double safe_z = std::abs(result.sz) > 1.0e-12 ? result.sz : 1.0;
    const double r00 = matrix[0] / safe_x;
    const double r10 = matrix[4] / safe_x;
    const double r20 = matrix[8] / safe_x;
    const double r01 = matrix[1] / safe_y;
    const double r11 = matrix[5] / safe_y;
    const double r21 = matrix[9] / safe_y;
    const double r02 = matrix[2] / safe_z;
    const double r12 = matrix[6] / safe_z;
    const double r22 = matrix[10] / safe_z;

    const double trace = r00 + r11 + r22;
    if (trace > 0.0) {
        const double s = std::sqrt(trace + 1.0) * 2.0;
        result.qw = 0.25 * s;
        result.qx = (r21 - r12) / s;
        result.qy = (r02 - r20) / s;
        result.qz = (r10 - r01) / s;
    } else if (r00 > r11 && r00 > r22) {
        const double s = std::sqrt(1.0 + r00 - r11 - r22) * 2.0;
        result.qw = (r21 - r12) / s;
        result.qx = 0.25 * s;
        result.qy = (r01 + r10) / s;
        result.qz = (r02 + r20) / s;
    } else if (r11 > r22) {
        const double s = std::sqrt(1.0 + r11 - r00 - r22) * 2.0;
        result.qw = (r02 - r20) / s;
        result.qx = (r01 + r10) / s;
        result.qy = 0.25 * s;
        result.qz = (r12 + r21) / s;
    } else {
        const double s = std::sqrt(1.0 + r22 - r00 - r11) * 2.0;
        result.qw = (r10 - r01) / s;
        result.qx = (r02 + r20) / s;
        result.qy = (r12 + r21) / s;
        result.qz = 0.25 * s;
    }

    const double q_length = std::sqrt(
        result.qx * result.qx + result.qy * result.qy +
        result.qz * result.qz + result.qw * result.qw);
    if (q_length > 1.0e-12) {
        result.qx /= q_length;
        result.qy /= q_length;
        result.qz /= q_length;
        result.qw /= q_length;
    } else {
        result.qx = result.qy = result.qz = 0.0;
        result.qw = 1.0;
    }
    return result;
}

inline Matrix4 ConjugateByScale(
    Matrix4 matrix,
    double scale_x,
    double scale_y,
    double scale_z) {
    const double sx = scale_x == 0.0 ? 1.0 : scale_x;
    const double sy = scale_y == 0.0 ? 1.0 : scale_y;
    const double sz = scale_z == 0.0 ? 1.0 : scale_z;
    const double row_scale[4] = { sx, sy, sz, 1.0 };
    const double column_scale[4] = { 1.0 / sx, 1.0 / sy, 1.0 / sz, 1.0 };
    for (int row = 0; row < 4; ++row)
        for (int column = 0; column < 4; ++column)
            matrix[row * 4 + column] *=
                row_scale[row] * column_scale[column];
    return matrix;
}

inline Matrix4 NodeMatrix(const tinygltf::Node& node) {
    if (node.matrix.size() == 16) {
        Matrix4 matrix{};
        for (int row = 0; row < 4; ++row)
            for (int column = 0; column < 4; ++column)
                matrix[row * 4 + column] = node.matrix[column * 4 + row];
        return matrix;
    }

    Transform transform;
    if (node.translation.size() == 3) {
        transform.tx = node.translation[0];
        transform.ty = node.translation[1];
        transform.tz = node.translation[2];
    }
    if (node.rotation.size() == 4) {
        transform.qx = node.rotation[0];
        transform.qy = node.rotation[1];
        transform.qz = node.rotation[2];
        transform.qw = node.rotation[3];
    }
    if (node.scale.size() == 3) {
        transform.sx = node.scale[0];
        transform.sy = node.scale[1];
        transform.sz = node.scale[2];
    }
    return ComposeMatrix(transform);
}

inline Transform NodeTransform(const tinygltf::Node& node) {
    return DecomposeMatrix(NodeMatrix(node));
}

inline std::vector<int> BuildNodeParents(const tinygltf::Model& model) {
    std::vector<int> parents(model.nodes.size(), -1);
    for (size_t parent = 0; parent < model.nodes.size(); ++parent) {
        for (int child : model.nodes[parent].children) {
            if (child >= 0 && child < static_cast<int>(model.nodes.size()))
                parents[child] = static_cast<int>(parent);
        }
    }
    return parents;
}

inline std::vector<Matrix4> ComputeNodeGlobals(
    const tinygltf::Model& model,
    const std::vector<int>& parents,
    const std::vector<Transform>* override_transforms = nullptr) {
    std::vector<Matrix4> globals(model.nodes.size(), IdentityMatrix());
    std::vector<uint8_t> state(model.nodes.size(), 0);
    std::function<void(int)> resolve = [&](int node_index) {
        if (node_index < 0 || node_index >= static_cast<int>(model.nodes.size()))
            return;
        if (state[node_index] == 2)
            return;
        if (state[node_index] == 1) {
            globals[node_index] = IdentityMatrix();
            state[node_index] = 2;
            return;
        }
        state[node_index] = 1;
        Matrix4 local = override_transforms
            ? ComposeMatrix((*override_transforms)[node_index])
            : NodeMatrix(model.nodes[node_index]);
        const int parent = parents[node_index];
        if (parent >= 0) {
            resolve(parent);
            globals[node_index] = MultiplyMatrix(globals[parent], local);
        } else {
            globals[node_index] = local;
        }
        state[node_index] = 2;
    };
    for (size_t node = 0; node < model.nodes.size(); ++node)
        resolve(static_cast<int>(node));
    return globals;
}

inline Matrix4 ReadMatrix(
    const std::vector<double>& values,
    size_t offset) {
    Matrix4 matrix = IdentityMatrix();
    if (offset + 16 > values.size())
        return matrix;
    // glTF matrices are serialized column-major. Decode them into the
    // shader's column-vector form; inverse binds are transposed to the
    // System.Numerics CPU representation before they are serialized.
    for (int row = 0; row < 4; ++row)
        for (int column = 0; column < 4; ++column)
            matrix[row * 4 + column] = values[offset + column * 4 + row];
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
    for (size_t bone = 0; bone < result.joint_nodes.size(); ++bone) {
        const int node_index = result.joint_nodes[bone];
        if (node_index < 0 || node_index >= static_cast<int>(model.nodes.size()))
            return {};
        node_to_bone[node_index] = static_cast<int>(bone);
    }

    const std::vector<int> node_parents = BuildNodeParents(model);
    const std::vector<Matrix4> bind_globals =
        ComputeNodeGlobals(model, node_parents);

    result.parent_indices.assign(result.joint_nodes.size(), -1);
    result.reference_pose.resize(result.joint_nodes.size());
    for (size_t bone = 0; bone < result.joint_nodes.size(); ++bone) {
        const int node_index = result.joint_nodes[bone];
        int ancestor = node_parents[node_index];
        while (ancestor >= 0 && node_to_bone.count(ancestor) == 0)
            ancestor = node_parents[ancestor];

        Matrix4 local = bind_globals[node_index];
        if (ancestor >= 0) {
            result.parent_indices[bone] = node_to_bone[ancestor];
            Matrix4 inverse_parent{};
            if (!InvertMatrix(bind_globals[ancestor], inverse_parent))
                return {};
            local = MultiplyMatrix(inverse_parent, bind_globals[node_index]);
        }

        local = ConjugateByScale(local, scale_x, scale_y, scale_z);
        result.reference_pose[bone] = DecomposeMatrix(local);
    }

    const tinygltf::Skin& skin = model.skins[result.skin_index];
    if (skin.skeleton >= 0 && node_to_bone.count(skin.skeleton) != 0) {
        result.root_bone = node_to_bone[skin.skeleton];
    } else {
        result.root_bone = 0;
        for (size_t bone = 0; bone < result.parent_indices.size(); ++bone) {
            if (result.parent_indices[bone] < 0) {
                result.root_bone = static_cast<int>(bone);
                break;
            }
        }
    }

    result.inverse_bind.assign(result.joint_nodes.size(), IdentityMatrix());
    if (skin.inverseBindMatrices >= 0) {
        const std::vector<double> values =
            ReadAccessor(model, skin.inverseBindMatrices);
        const size_t matrix_count = values.size() / 16;
        const size_t count = std::min(matrix_count, result.inverse_bind.size());
        for (size_t bone = 0; bone < count; ++bone) {
            result.inverse_bind[bone] = TransposeMatrix(
                ConjugateByScale(
                    ReadMatrix(values, bone * 16),
                    scale_x,
                    scale_y,
                    scale_z));
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
    if (times.empty() || values.size() < static_cast<size_t>(component_count))
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
            std::vector<Transform>(skeleton.joint_nodes.size()));
        const std::vector<int> node_parents = BuildNodeParents(model);
        std::map<int, int> node_to_bone;
        for (size_t bone = 0; bone < skeleton.joint_nodes.size(); ++bone)
            node_to_bone[skeleton.joint_nodes[bone]] = static_cast<int>(bone);

        std::vector<Transform> reference_nodes(model.nodes.size());
        for (size_t node = 0; node < model.nodes.size(); ++node)
            reference_nodes[node] = NodeTransform(model.nodes[node]);

        for (int frame = 0; frame < frame_count; ++frame) {
            const double time = std::min(
                duration, frame / static_cast<double>(sample_rate));
            std::vector<Transform> animated_nodes = reference_nodes;

            for (const tinygltf::AnimationChannel& channel : animation.channels) {
                if (channel.target_node < 0 ||
                    channel.target_node >= static_cast<int>(animated_nodes.size()) ||
                    channel.sampler < 0 ||
                    channel.sampler >= static_cast<int>(animation.samplers.size()))
                    continue;
                animated_nodes[channel.target_node] = SampleChannel(
                    model,
                    animation.samplers[channel.sampler],
                    channel.target_path,
                    time,
                    animated_nodes[channel.target_node]);
            }

            const std::vector<Matrix4> globals =
                ComputeNodeGlobals(model, node_parents, &animated_nodes);
            for (size_t bone = 0; bone < skeleton.joint_nodes.size(); ++bone) {
                const int joint_node = skeleton.joint_nodes[bone];
                Matrix4 local = globals[joint_node];
                const int parent_bone = skeleton.parent_indices[bone];
                if (parent_bone >= 0 &&
                    parent_bone < static_cast<int>(skeleton.joint_nodes.size())) {
                    const int parent_node = skeleton.joint_nodes[parent_bone];
                    Matrix4 inverse_parent{};
                    if (InvertMatrix(globals[parent_node], inverse_parent))
                        local = MultiplyMatrix(inverse_parent, globals[joint_node]);
                }
                local = ConjugateByScale(
                    local, scale_x, scale_y, scale_z);
                frames[frame][bone] = DecomposeMatrix(local);
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
