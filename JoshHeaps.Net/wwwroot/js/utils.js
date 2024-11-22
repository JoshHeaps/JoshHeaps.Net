function typeText(selector, content) {
    const element = document.querySelector(selector);

    if (!element) {
        console.log("Element not found.");
        return;
    }

    simulateTyping(element, content);
}

function simulateTyping(element, text) {
    let index = 0;
    let speed = 50;
    let currentText = "";

    const interval = setInterval(() => {
        if (index < text.length) {
            currentText += text.charAt(index);
            index++;
            element.textContent = currentText;
        } else {
            clearInterval(interval);
        }
    }, speed);
}