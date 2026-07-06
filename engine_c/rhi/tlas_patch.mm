        } else {
            MTLInstanceAccelerationStructureDescriptor* inst_desc = [MTLInstanceAccelerationStructureDescriptor descriptor];
            if (@available(macOS 13.0, iOS 16.0, *)) {
                inst_desc.instanceDescriptorType = MTLAccelerationStructureInstanceDescriptorTypeUserID;
            }
            inst_desc.instanceCount = desc->instance_count;
            
            id<MTLBuffer> ibuf = nil;
            if (desc->instance_count > 0) {
                ibuf = [di->device newBufferWithLength:sizeof(MTLAccelerationStructureUserIDInstanceDescriptor) * desc->instance_count options:MTLResourceStorageModeShared];
                MTLAccelerationStructureUserIDInstanceDescriptor* ptr = (MTLAccelerationStructureUserIDInstanceDescriptor*)[ibuf contents];
                NSMutableArray<id<MTLAccelerationStructure>>* blas_array = [NSMutableArray arrayWithCapacity:desc->instance_count];
                
                for (uint32_t i = 0; i < desc->instance_count; i++) {
                    const RhiTlasInstanceDesc* src = &desc->instances[i];
                    
                    ptr[i].transformationMatrix[0][0] = src->transform[0];
                    ptr[i].transformationMatrix[0][1] = src->transform[4];
                    ptr[i].transformationMatrix[0][2] = src->transform[8];
                    ptr[i].transformationMatrix[1][0] = src->transform[1];
                    ptr[i].transformationMatrix[1][1] = src->transform[5];
                    ptr[i].transformationMatrix[1][2] = src->transform[9];
                    ptr[i].transformationMatrix[2][0] = src->transform[2];
                    ptr[i].transformationMatrix[2][1] = src->transform[6];
                    ptr[i].transformationMatrix[2][2] = src->transform[10];
                    ptr[i].transformationMatrix[3][0] = src->transform[3];
                    ptr[i].transformationMatrix[3][1] = src->transform[7];
                    ptr[i].transformationMatrix[3][2] = src->transform[11];
                    
                    ptr[i].options = src->flags;
                    ptr[i].mask = src->mask;
                    ptr[i].intersectionFunctionTableOffset = src->instance_offset;
                    ptr[i].accelerationStructureIndex = i;
                    ptr[i].userID = src->instance_id;
                    
                    RhiAccelStructImpl* blas_impl = reinterpret_cast<RhiAccelStructImpl*>(src->blas);
                    [blas_array addObject:blas_impl ? blas_impl->as : (id<MTLAccelerationStructure>)[NSNull null]];
                }
                
                inst_desc.instanceDescriptorBuffer = ibuf;
                inst_desc.instanceDescriptorBufferOffset = 0;
                inst_desc.instancedAccelerationStructures = blas_array;
            }
            mtl_desc = inst_desc;
        }
