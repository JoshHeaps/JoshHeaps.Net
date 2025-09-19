document.addEventListener('DOMContentLoaded', () => {
    getIpStatus();
});

async function getIpStatus() {
    try {
        const res = await fetch(`/api/debug/IpCheck`);
        if (!res.ok) throw new Error("API failed");

        const text = await res.text();
        console.log(text);
        document.querySelector('#IsIpChecking').textContent = text;
    } catch (err) {
        console.error("Error in api:", err);
    }
}