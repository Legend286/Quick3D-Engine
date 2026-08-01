// SPDX-License-Identifier: MIT

struct ResetPushData
{
    uint* probeCounter;
    uint* probeDrawArgs;
    uint* volumeState;
    uint probeCapacity;
    uint padding0;
};

[push_constant]
ConstantBuffer<ResetPushData> push;

[shader("compute")]
[NumThreads(1, 1, 1)]
void resetMain()
{
    push.probeCounter[0] = 0u;
    push.probeCounter[1] = 0u;
    push.probeCounter[2] = 0u;
    push.probeCounter[3] = 0u;
    push.probeDrawArgs[0] = push.probeCapacity * 24u;
    push.probeDrawArgs[1] = 1u;
    push.probeDrawArgs[2] = 0u;
    push.probeDrawArgs[3] = 0u;
}
