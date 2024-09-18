// Copyright (c) Microsoft. All rights reserved.

using Microsoft.KernelMemory.Enums;

namespace Microsoft.KernelMemory.Models;
public class PromptSegment
{
    public PromptSegment(ChatRoles chatRole, string message)
    {
        this.ChatRole = chatRole;
        this.Message = message;
    }
    public ChatRoles ChatRole { get; }
    public string Message { get; }
}
