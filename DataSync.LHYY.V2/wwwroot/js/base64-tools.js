(function () {
    "use strict";

    const maximumFileBytes = 20 * 1024 * 1024;
    const chunkSize = 32 * 1024;

    function getElement(id) {
        return document.getElementById(id);
    }

    function setStatus(id, message, type) {
        const status = getElement(id);
        if (!status) {
            return;
        }

        status.textContent = message || "";
        if (type) {
            status.dataset.type = type;
        } else {
            delete status.dataset.type;
        }
    }

    function bytesToBase64(bytes) {
        const parts = [];
        for (let offset = 0; offset < bytes.length; offset += chunkSize) {
            const chunk = bytes.subarray(offset, Math.min(offset + chunkSize, bytes.length));
            let binary = "";
            for (let index = 0; index < chunk.length; index++) {
                binary += String.fromCharCode(chunk[index]);
            }
            parts.push(binary);
        }

        return btoa(parts.join(""));
    }

    function normalizeBase64(value) {
        let normalized = (value || "").replace(/\s/g, "");
        if (!normalized) {
            throw new Error("请输入 Base64 内容。");
        }
        if (normalized.toLowerCase().startsWith("data:")) {
            throw new Error("请输入不带 data:*;base64, 前缀的纯 Base64。");
        }
        if (!/^[A-Za-z0-9+/]*={0,2}$/.test(normalized) || normalized.length % 4 === 1) {
            throw new Error("Base64 格式不正确。");
        }

        normalized += "=".repeat((4 - normalized.length % 4) % 4);
        return normalized;
    }

    function getDecodedSize(base64) {
        const padding = base64.endsWith("==") ? 2 : base64.endsWith("=") ? 1 : 0;
        return base64.length / 4 * 3 - padding;
    }

    function base64ToBytes(value, enforceFileLimit) {
        const normalized = normalizeBase64(value);
        if (enforceFileLimit && getDecodedSize(normalized) > maximumFileBytes) {
            throw new Error("解码后的文件超过 20 MB，无法处理。");
        }

        let binary;
        try {
            binary = atob(normalized);
        } catch {
            throw new Error("Base64 格式不正确。");
        }

        const bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index++) {
            bytes[index] = binary.charCodeAt(index);
        }
        return bytes;
    }

    function encodeText() {
        const input = getElement("base64-text-encode-input");
        const output = getElement("base64-text-encode-output");
        if (!input || !output) {
            return;
        }

        if (!input.value) {
            setStatus("base64-text-encode-status", "请输入需要转换的文字。", "error");
            return;
        }

        output.value = bytesToBase64(new TextEncoder().encode(input.value));
        setStatus("base64-text-encode-status", `转换完成，共 ${output.value.length.toLocaleString()} 个 Base64 字符。`, "success");
    }

    function decodeText() {
        const input = getElement("base64-text-decode-input");
        const output = getElement("base64-text-decode-output");
        if (!input || !output) {
            return;
        }

        try {
            const bytes = base64ToBytes(input.value, false);
            output.value = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
            setStatus("base64-text-decode-status", `转换完成，共 ${output.value.length.toLocaleString()} 个文字字符。`, "success");
        } catch (error) {
            output.value = "";
            const message = error instanceof TypeError ? "解码结果不是有效的 UTF-8 文字。" : error.message;
            setStatus("base64-text-decode-status", message, "error");
        }
    }

    async function encodeFile() {
        const input = getElement("base64-file-encode-input");
        const output = getElement("base64-file-encode-output");
        const file = input && input.files ? input.files[0] : null;
        if (!input || !output || !file) {
            setStatus("base64-file-encode-status", "请先选择需要转换的文件。", "error");
            return;
        }
        if (file.size > maximumFileBytes) {
            output.value = "";
            setStatus("base64-file-encode-status", "文件超过 20 MB，无法处理。", "error");
            return;
        }

        try {
            setStatus("base64-file-encode-status", "正在转换，请稍候……");
            const bytes = new Uint8Array(await file.arrayBuffer());
            output.value = bytesToBase64(bytes);
            setStatus("base64-file-encode-status", `转换完成，原文件 ${formatBytes(file.size)}，结果 ${output.value.length.toLocaleString()} 个字符。`, "success");
        } catch {
            output.value = "";
            setStatus("base64-file-encode-status", "文件读取失败，请重新选择后再试。", "error");
        }
    }

    function downloadFile() {
        const input = getElement("base64-file-decode-input");
        const nameInput = getElement("base64-download-name");
        if (!input || !nameInput) {
            return;
        }

        try {
            const bytes = base64ToBytes(input.value, true);
            const fileName = sanitizeFileName(nameInput.value);
            const blob = new Blob([bytes], { type: "application/octet-stream" });
            const url = URL.createObjectURL(blob);
            const link = document.createElement("a");
            link.href = url;
            link.download = fileName;
            document.body.appendChild(link);
            link.click();
            link.remove();
            URL.revokeObjectURL(url);
            setStatus("base64-file-decode-status", `转换完成，已下载 ${fileName}（${formatBytes(bytes.length)}）。`, "success");
        } catch (error) {
            setStatus("base64-file-decode-status", error.message, "error");
        }
    }

    async function copyValue(targetId) {
        const target = getElement(targetId);
        if (!target || !target.value) {
            setCopyStatus(targetId, "没有可复制的内容。", "error");
            return;
        }

        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(target.value);
            } else {
                target.focus();
                target.select();
                if (!document.execCommand("copy")) {
                    throw new Error();
                }
                target.setSelectionRange(0, 0);
            }
            setCopyStatus(targetId, "已复制到剪贴板。", "success");
        } catch {
            setCopyStatus(targetId, "复制失败，请手动选择内容复制。", "error");
        }
    }

    function setCopyStatus(targetId, message, type) {
        const statusMap = {
            "base64-text-encode-output": "base64-text-encode-status",
            "base64-text-decode-output": "base64-text-decode-status",
            "base64-file-encode-output": "base64-file-encode-status"
        };
        setStatus(statusMap[targetId], message, type);
    }

    function sanitizeFileName(value) {
        const parts = (value || "").trim().split(/[\\/]/);
        return parts[parts.length - 1] || "decoded-file.bin";
    }

    function formatBytes(bytes) {
        if (bytes < 1024) {
            return `${bytes} B`;
        }
        if (bytes < 1024 * 1024) {
            return `${(bytes / 1024).toFixed(1)} KB`;
        }
        return `${(bytes / 1024 / 1024).toFixed(2)} MB`;
    }

    function clearValues(ids, statusId) {
        ids.forEach(id => {
            const element = getElement(id);
            if (element) {
                element.value = "";
            }
        });
        setStatus(statusId, "");
    }

    function selectMode(mode) {
        document.querySelectorAll("[data-base64-mode]").forEach(button => {
            const isActive = button.dataset.base64Mode === mode;
            button.classList.toggle("active", isActive);
            button.setAttribute("aria-selected", isActive ? "true" : "false");
        });

        document.querySelectorAll("[data-base64-panel]").forEach(panel => {
            panel.hidden = panel.dataset.base64Panel !== mode;
        });
    }

    const actions = {
        "encode-text": encodeText,
        "decode-text": decodeText,
        "encode-file": encodeFile,
        "download-file": downloadFile,
        "clear-text-encode": () => clearValues(["base64-text-encode-input", "base64-text-encode-output"], "base64-text-encode-status"),
        "clear-text-decode": () => clearValues(["base64-text-decode-input", "base64-text-decode-output"], "base64-text-decode-status"),
        "clear-file-encode": () => {
            clearValues(["base64-file-encode-input", "base64-file-encode-output"], "base64-file-encode-status");
            const fileName = getElement("base64-file-name");
            if (fileName) {
                fileName.textContent = "尚未选择文件";
            }
        },
        "clear-file-decode": () => {
            clearValues(["base64-file-decode-input"], "base64-file-decode-status");
            const nameInput = getElement("base64-download-name");
            if (nameInput) {
                nameInput.value = "decoded-file.bin";
            }
        }
    };

    document.addEventListener("click", event => {
        const modeButton = event.target.closest("[data-base64-mode]");
        if (modeButton) {
            selectMode(modeButton.dataset.base64Mode);
            return;
        }

        const button = event.target.closest("[data-base64-action]");
        if (!button) {
            return;
        }

        const action = button.dataset.base64Action;
        if (action === "copy") {
            copyValue(button.dataset.base64Target);
            return;
        }

        if (actions[action]) {
            actions[action]();
        }
    });

    document.addEventListener("change", event => {
        if (event.target.id !== "base64-file-encode-input") {
            return;
        }

        const file = event.target.files ? event.target.files[0] : null;
        const fileName = getElement("base64-file-name");
        const output = getElement("base64-file-encode-output");
        if (fileName) {
            fileName.textContent = file ? `${file.name} · ${formatBytes(file.size)}` : "尚未选择文件";
        }
        if (output) {
            output.value = "";
        }
        setStatus("base64-file-encode-status", file && file.size > maximumFileBytes ? "文件超过 20 MB，无法处理。" : "", file && file.size > maximumFileBytes ? "error" : "");
    });
})();
