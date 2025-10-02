window.onload = function () {
    const element = document.getElementById("WelcomeMessage");
    element.style.display = "none"; // Trigger reflow
    element.offsetHeight; // Force reflow
    element.style.display = ""; // Restore the original display
    typeText("#WelcomeMessage", "Today, October 2nd, marks the 5th anniversary of the day I was sealed for time and all eternity to my wonderful wife. She's the kindest, sweetest, funniest, most beautiful woman I've ever met. I love you Morgan 💖");
}