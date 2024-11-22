window.onload = function () {
    const element = document.getElementById("WelcomeMessage");
    element.style.display = "none"; // Trigger reflow
    element.offsetHeight; // Force reflow
    element.style.display = ""; // Restore the original display
    typeText("#WelcomeMessage", "Hey! You found me! Since I have your attention, why don't I tell you about myself?");
}