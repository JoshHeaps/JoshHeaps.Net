
document.addEventListener('DOMContentLoaded', () => {
    const canvas = document.getElementById('gameCanvas');
    const ctx = canvas.getContext('2d');

    canvas.width = window.innerWidth;
    canvas.height = window.innerHeight;

    window.addEventListener('resize', () => {
        canvas.width = window.innerWidth;
        canvas.height = window.innerHeight;
    });

    // Game state
    const windows = [
        { x: 100, y: 100, width: 200, height: 150, isDragging: false, offsetX: 0, offsetY: 0 },
        { x: 400, y: 300, width: 200, height: 150, isDragging: false, offsetX: 0, offsetY: 0 }
    ];

    const particles = [];

    // Event handling
    canvas.addEventListener('mousedown', (e) => {
        const mouse = { x: e.clientX, y: e.clientY };
        windows.forEach(win => {
            if (
                mouse.x >= win.x && mouse.x <= win.x + win.width &&
                mouse.y >= win.y && mouse.y <= win.y + win.height
            ) {
                win.isDragging = true;
                win.offsetX = mouse.x - win.x;
                win.offsetY = mouse.y - win.y;
            }
        });
    });

    canvas.addEventListener('mousemove', (e) => {
        windows.forEach(win => {
            if (win.isDragging) {
                win.x = e.clientX - win.offsetX;
                win.y = e.clientY - win.offsetY;
            }
        });
    });

    canvas.addEventListener('mouseup', () => {
        windows.forEach(win => win.isDragging = false);
    });

    
    // Game loop
    function gameLoop() {
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        ctx.fillStyle = 'rgba(0, 0, 0, 1)';
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        windows.forEach(win => {
            ctx.save();
            ctx.globalCompositeOperation = 'destination-out';
            ctx.beginPath();
            ctx.roundRect(win.x, win.y, win.width, win.height, 10);
            ctx.fill();
            ctx.restore();

            // Optional: draw a visible outline
            ctx.strokeStyle = '#ccc';
            ctx.lineWidth = 2;
            ctx.stroke();
        });

        // Draw windows
        windows.forEach(win => {
            ctx.fillStyle = "rgba(255,255,255,0)";
            ctx.strokeStyle = "#ccc";
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.circle(win.x, win.y, win.width, 10);
            ctx.fill();
            ctx.stroke();
        });

        requestAnimationFrame(gameLoop);
    }

    gameLoop();
});