// Small helpers called from Blazor via JS interop.
window.elektra = {
    scrollToBottom: () => window.scrollTo({ top: document.body.scrollHeight, behavior: "smooth" }),
    downloadText: (fileName, text) => {
        const blob = new Blob(["﻿" + text], { type: "text/plain;charset=utf-8" });
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName;
        a.click();
        URL.revokeObjectURL(url);
    }
};
