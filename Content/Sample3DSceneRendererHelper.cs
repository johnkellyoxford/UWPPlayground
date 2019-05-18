﻿using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TerraFX.Interop;
using UWPPlayground.Common;
using UWPPlayground.Common.d3dx12;
using static TerraFX.Interop.DXGI_FORMAT;
using static TerraFX.Interop.D3D12_INPUT_CLASSIFICATION;
using static UWPPlayground.Common.d3dx12.CD3DX12_DEFAULT;
using static UWPPlayground.Common.DirectXHelper;
using static TerraFX.Interop.D3D12;
using UWPPlayground.Content;

using D3D12_GPU_VIRTUAL_ADDRESS = System.UInt64;

namespace UWPPlayground.Content
{
    public partial class Sample3DSceneRenderer
    {
        public static void CopyBytesToBlob(out ComPtrField<ID3DBlob> blob, UIntPtr size, byte[] bytes)
        {
            Span<byte> span = CreateBlob(out blob, size);
            bytes.CopyTo(span);
        }

        public async Task ReadVertexShader()
        {
            const string fileName = "SampleVertexShader.cso";

            var size = (UIntPtr)new FileInfo(fileName).Length;
            byte[] shader = await File.ReadAllBytesAsync(fileName);
            CopyBytesToBlob(out _vertexShader, size, shader);
        }

        public async Task ReadPixelShader()
        {
            const string fileName = "SamplePixelShader.cso";

            var size = (UIntPtr)new FileInfo(fileName).Length;
            byte[] shader = await File.ReadAllBytesAsync(fileName);
            CopyBytesToBlob(out _pixelShader, size, shader);
        }

        private static unsafe Span<byte> CreateBlob(out ComPtrField<ID3DBlob> ppBlob, UIntPtr size)
        {
#if DEBUG
            ppBlob = null;
#endif
            ID3DBlob* p;

            ThrowIfFailed(D3DCompiler.D3DCreateBlob(size, &p));

            ppBlob = p;

            return new Span<byte>(ppBlob.Get()->GetBufferPointer(), (int)ppBlob.Get()->GetBufferSize());
        }

        private unsafe void CreatePipelineDescAndPipelineState()
        {
            sbyte* pColor = stackalloc sbyte[]
             {
                (sbyte)'C',
                (sbyte)'O',
                (sbyte)'L',
                (sbyte)'O',
                (sbyte)'R',
                (sbyte)'\0'
            };

            sbyte* pPosition = stackalloc sbyte[]
            {
                (sbyte)'P',
                (sbyte)'O',
                (sbyte)'S',
                (sbyte)'I',
                (sbyte)'T',
                (sbyte)'I',
                (sbyte)'O',
                (sbyte)'N',
                (sbyte)'\0'
            };

            D3D12_INPUT_ELEMENT_DESC* pInputLayout = stackalloc D3D12_INPUT_ELEMENT_DESC[]
            {
                new D3D12_INPUT_ELEMENT_DESC
                {
                    SemanticName = pPosition,
                    SemanticIndex = 0,
                    Format = DXGI_FORMAT_R32G32B32_FLOAT,
                    InputSlot = 0,
                    AlignedByteOffset = 0,
                    InputSlotClass = D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,
                    InstanceDataStepRate = 0
                },
                new D3D12_INPUT_ELEMENT_DESC
                {
                    SemanticName = pColor,
                    SemanticIndex = 0,
                    Format = DXGI_FORMAT_R32G32B32_FLOAT,
                    InputSlot = 0,
                    AlignedByteOffset = 12,
                    InputSlotClass = D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA,
                    InstanceDataStepRate = 0
                }
            };

            D3D12_GRAPHICS_PIPELINE_STATE_DESC state;
            state.InputLayout = new D3D12_INPUT_LAYOUT_DESC
            {
                pInputElementDescs = pInputLayout,
                NumElements = 2
            };
            state.pRootSignature = _rootSignature.Get();
            state.VS = CD3DX12_SHADER_BYTECODE.Create(_vertexShader.Get()->GetBufferPointer(),
                _vertexShader.Get()->GetBufferSize());

            state.PS = CD3DX12_SHADER_BYTECODE.Create(_pixelShader.Get()->GetBufferPointer(),
                _pixelShader.Get()->GetBufferSize());

            state.RasterizerState = CD3DX12_RASTERIZER_DESC.Create(D3D12_DEFAULT);
            state.BlendState = CD3DX12_BLEND_DESC.Create(D3D12_DEFAULT);
            state.DepthStencilState = CD3DX12_DEPTH_STENCIL_DESC.Create(D3D12_DEFAULT);
            state.SampleMask = uint.MaxValue;
            state.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
            state.NumRenderTargets = 1;
            state.RTVFormats = default; // TODO redundant init - any solution?
            state.RTVFormats[0] = _deviceResources.BackBufferFormat;
            state.DSVFormat = _deviceResources.DepthBufferFormat;
            state.SampleDesc.Count = 1;

            {
                Guid iid = IID_ID3D12PipelineState;
                ID3D12PipelineState* pipelineState;
                ThrowIfFailed(
                    _deviceResources.D3DDevice->CreateGraphicsPipelineState(
                        &state,
                        &iid,
                        (void**)&pipelineState)
                );
                _pipelineState = pipelineState;
            }
        }

        public async Task CreatePipelineState(Task vertexShaderTask, Task pixelShaderTask)
        {
            await vertexShaderTask;
            await pixelShaderTask;

            CreatePipelineDescAndPipelineState();
        }

        public async Task CreateRendererAssets(Task pipelineTask)
        {
            await pipelineTask;
            CreateAssets();
            _loadingComplete = true;
        }

        private unsafe void CreateAssets()
        {
            ID3D12Device* d3dDevice = _deviceResources.D3DDevice;

            Guid iid;

            {
                iid = IID_ID3D12GraphicsCommandList;
                ID3D12GraphicsCommandList* commandList;
                ThrowIfFailed(d3dDevice->CreateCommandList(
                    0,
                    D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
                    _deviceResources.CommandAllocator,
                    _pipelineState.Get(),
                    &iid,
                    (void**)&commandList));

                _commandList = commandList;
                NameObject(_commandList, nameof(_commandList));
            }

            // Cube vertices. Each vertex has a position and a color.
            const uint vertexPositionColorCount = 8;
            VertexPositionColor* cubeVertices = stackalloc VertexPositionColor[(int)vertexPositionColorCount]
            {
                new VertexPositionColor { pos = new Vector3(-0.5f, -0.5f, -0.5f), color = new Vector3(0.0f, 0.0f, 0.0f) },
                new VertexPositionColor { pos = new Vector3(-0.5f, -0.5f,  0.5f), color = new Vector3(0.0f, 0.0f, 1.0f) },
                new VertexPositionColor { pos = new Vector3(-0.5f,  0.5f, -0.5f), color = new Vector3(0.0f, 1.0f, 0.0f) },
                new VertexPositionColor { pos = new Vector3(-0.5f,  0.5f,  0.5f), color = new Vector3(0.0f, 1.0f, 1.0f) },
                new VertexPositionColor { pos = new Vector3( 0.5f, -0.5f, -0.5f), color = new Vector3(1.0f, 0.0f, 0.0f) },
                new VertexPositionColor { pos = new Vector3( 0.5f, -0.5f,  0.5f), color = new Vector3(1.0f, 0.0f, 1.0f) },
                new VertexPositionColor { pos = new Vector3( 0.5f,  0.5f, -0.5f), color = new Vector3(1.0f, 1.0f, 0.0f) },
                new VertexPositionColor { pos = new Vector3( 0.5f,  0.5f,  0.5f), color = new Vector3(1.0f, 1.0f, 1.0f) }
            };
            uint vertexBufferSize = (uint)sizeof(VertexPositionColor) * vertexPositionColorCount;

            using ComPtr<ID3D12Resource> vertexBufferUpload = default;

            D3D12_HEAP_PROPERTIES defaultHeapProperties =
                CD3DX12_HEAP_PROPERTIES.Create(D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT);

            D3D12_RESOURCE_DESC vertexBufferDesc = CD3DX12_RESOURCE_DESC.Buffer(vertexBufferSize);

            {
                iid = IID_ID3D12Resource;
                ID3D12Resource* vertexBuffer;
                ThrowIfFailed(d3dDevice->CreateCommittedResource(
                    &defaultHeapProperties,
                    D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
                    &vertexBufferDesc,
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
                    null,
                    &iid,
                    (void**)&vertexBuffer));

                _vertexBuffer = vertexBuffer;
            }

            iid = IID_ID3D12Resource;
            D3D12_HEAP_PROPERTIES uploadHeapProperties =
                CD3DX12_HEAP_PROPERTIES.Create(D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD);

            ThrowIfFailed(d3dDevice->CreateCommittedResource(
                &uploadHeapProperties,
                D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
                &vertexBufferDesc,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ,
                null,
                &iid,
                (void**)vertexBufferUpload.GetAddressOf()));

            NameObject(_vertexBuffer, nameof(_vertexBuffer));

            {
                D3D12_SUBRESOURCE_DATA vertexData;
                vertexData.pData = (byte*)cubeVertices;
                vertexData.RowPitch = (IntPtr)vertexBufferSize;
                vertexData.SlicePitch = vertexData.RowPitch;

                Functions.UpdateSubresources(
                    _commandList.Get(),
                    _vertexBuffer.Get(),
                    vertexBufferUpload.Get(),
                    0, 0, 1,
                    &vertexData);

                D3D12_RESOURCE_BARRIER vertexBufferResourceBarrier =
                    CD3DX12_RESOURCE_BARRIER.Transition(_vertexBuffer.Get(),
                        D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
                        D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_VERTEX_AND_CONSTANT_BUFFER);

                _commandList.Get()->ResourceBarrier(1, &vertexBufferResourceBarrier);
            }

            const int cubeIndicesCount = 36;
            ushort* cubeIndices = stackalloc ushort[cubeIndicesCount]
            {
                0,
                2,
                1, // -x
                1,
                2,
                3,

                4,
                5,
                6, // +x
                5,
                7,
                6,

                0,
                1,
                5, // -y
                0,
                5,
                4,

                2,
                6,
                7, // +y
                2,
                7,
                3,

                0,
                4,
                6, // -z
                0,
                6,
                2,

                1,
                3,
                7, // +z
                1,
                7,
                5,
            };
            const uint indexBufferSize = sizeof(ushort) * cubeIndicesCount;

            using var indexBufferUpload = new ComPtr<ID3D12Resource>();

            D3D12_RESOURCE_DESC indexBufferDesc = CD3DX12_RESOURCE_DESC.Buffer(indexBufferSize);

            {
                iid = IID_ID3D12Resource;
                ID3D12Resource* indexBuffer;
                ThrowIfFailed(d3dDevice->CreateCommittedResource(
                    &defaultHeapProperties,
                    D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
                    &indexBufferDesc,
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
                    null,
                    &iid,
                    (void**)&indexBuffer));
                _indexBuffer = indexBuffer;
            }

            iid = IID_ID3D12Resource;
            ThrowIfFailed(d3dDevice->CreateCommittedResource(
                &uploadHeapProperties,
                D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
                &indexBufferDesc,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ,
                null,
                &iid,
                (void**)indexBufferUpload.GetAddressOf()));

            NameObject(_indexBuffer, nameof(_indexBuffer));

            {
                D3D12_SUBRESOURCE_DATA indexData;
                indexData.pData = (byte*)cubeIndices;
                indexData.RowPitch = (IntPtr)indexBufferSize;
                indexData.SlicePitch = indexData.RowPitch;

                Functions.UpdateSubresources(
                    _commandList.Get(),
                    _indexBuffer.Get(),
                    indexBufferUpload.Get(),
                    0, 0, 1,
                    &indexData);

                D3D12_RESOURCE_BARRIER indexBufferResourceBarrier =
                    CD3DX12_RESOURCE_BARRIER.Transition(_indexBuffer.Get(),
                        D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
                        D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_INDEX_BUFFER);

                _commandList.Get()->ResourceBarrier(1, &indexBufferResourceBarrier);
            }

            {
                D3D12_DESCRIPTOR_HEAP_DESC heapDesc;
                heapDesc.NumDescriptors = DeviceResources.FrameCount;
                heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
                heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

                {
                    ID3D12DescriptorHeap* cbvHeap;
                    iid = IID_ID3D12DescriptorHeap;
                    ThrowIfFailed(d3dDevice->CreateDescriptorHeap(&heapDesc, &iid, (void**)&cbvHeap));
                    _cbvHeap = cbvHeap;
                    NameObject(_cbvHeap, nameof(_cbvHeap));
                }
            }

            D3D12_RESOURCE_DESC constantBufferDesc = CD3DX12_RESOURCE_DESC.Buffer(
                DeviceResources.FrameCount * AlignedConstantBufferSize);

            ID3D12Resource* constantBuffer;
            {
                iid = IID_ID3D12Resource;
                ThrowIfFailed(d3dDevice->CreateCommittedResource(
                    &uploadHeapProperties,
                    D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
                    &constantBufferDesc,
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ,
                    null,
                    &iid,
                    (void**)&constantBuffer));
                _constantBuffer = constantBuffer;

                NameObject(_constantBuffer, nameof(_constantBuffer));
            }

            D3D12_GPU_VIRTUAL_ADDRESS cbvGpuAddress = _constantBuffer.Get()->GetGPUVirtualAddress();
            D3D12_CPU_DESCRIPTOR_HANDLE cbvCpuHandle;
            _cbvHeap.Get()->GetCPUDescriptorHandleForHeapStart(&cbvCpuHandle);
            _cbvDescriptorSize =
                d3dDevice->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE
                    .D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

            for (var i = 0; i < DeviceResources.FrameCount; i++)
            {
                D3D12_CONSTANT_BUFFER_VIEW_DESC desc;
                desc.BufferLocation = cbvGpuAddress;
                desc.SizeInBytes = AlignedConstantBufferSize;
                d3dDevice->CreateConstantBufferView(&desc, cbvCpuHandle);
                cbvGpuAddress += desc.SizeInBytes;
                cbvCpuHandle.Offset((int)_cbvDescriptorSize);
            }

            D3D12_RANGE readRange = CD3DX12_RANGE.Create((UIntPtr)0, (UIntPtr)0);

            fixed (byte** p = &_mappedConstantBuffer)
            {
                ThrowIfFailed(_constantBuffer.Get()->Map(0, &readRange, (void**)p));
                Unsafe.InitBlockUnaligned(_mappedConstantBuffer, 0, DeviceResources.FrameCount * AlignedConstantBufferSize);
            }

            ThrowIfFailed(_commandList.Get()->Close());
            const int ppCommandListCount = 1;
            ID3D12CommandList** ppCommandLists = stackalloc ID3D12CommandList*[ppCommandListCount]
            {
                (ID3D12CommandList*)_commandList.Get()
            };

            _deviceResources.CommandQueue->ExecuteCommandLists(ppCommandListCount, ppCommandLists);

            _vertexBufferView.BufferLocation = _vertexBuffer.Get()->GetGPUVirtualAddress();
            _vertexBufferView.SizeInBytes = (uint)sizeof(VertexPositionColor);
            _vertexBufferView.SizeInBytes = vertexBufferSize;

            _indexBufferView.BufferLocation = _indexBuffer.Get()->GetGPUVirtualAddress();
            _indexBufferView.SizeInBytes = indexBufferSize;
            _indexBufferView.Format = DXGI_FORMAT_R16_UINT;

            _deviceResources.WaitForGpu();
        }
    }
}
