(ns csharp-interop-test
  (:require [clojure.test :refer :all])
  (:import [ClojureApp.Services DataProcessor MathUtils Person]))

(deftest test-data-processor
  (testing "DataProcessor instance methods and properties"
    (let [processor (DataProcessor. "TestProcessor")]
      (is (= "TestProcessor" (.Name processor)) "Name property should match constructor argument")
      (is (= 0 (.ProcessedCount processor)) "Initial count should be 0")
      
      (let [result1 (.ProcessData processor "hello")]
        (is (= "[TestProcessor] Processed: HELLO (#1)" result1) "Should process and uppercase input")
        (is (= 1 (.ProcessedCount processor)) "Count should increment to 1"))
      
      (let [result2 (.ProcessData processor "world")]
        (is (= "[TestProcessor] Processed: WORLD (#2)" result2) "Should process second input")
        (is (= 2 (.ProcessedCount processor)) "Count should increment to 2")))))

(deftest test-math-utils-static
  (testing "MathUtils static methods"
    (is (= 15 (MathUtils/Add 7 8)) "Add should work correctly")
    (is (= 42 (MathUtils/Multiply 6 7)) "Multiply should work correctly")
    (is (= 256.0 (MathUtils/Power 2.0 8.0)) "Power should calculate 2^8")
    (is (string? (MathUtils/FormatNumber 3.14159 2)) "FormatNumber should return string")))

(deftest test-person-class
  (testing "Person class properties and methods"
    (let [person (Person. "Jane" "Doe" 25)]
      (is (= "Jane" (.FirstName person)) "FirstName should be accessible")
      (is (= "Doe" (.LastName person)) "LastName should be accessible")
      (is (= 25 (.Age person)) "Age should be accessible")
      (is (= "Jane Doe" (.GetFullName person)) "GetFullName should combine names")
      
      ;; Test property setter
      (.set_Age person 26)
      (is (= 26 (.Age person)) "Age should be updated after setter")
      
      ;; Test ToString
      (is (string? (.ToString person)) "ToString should return a string")
      (is (.Contains (.ToString person) "Jane Doe") "ToString should contain the name"))))

(deftest test-csharp-clojure-integration
  (testing "Integration between C# objects and Clojure functions"
    ;; Create multiple processors and use them with Clojure functions
    (let [processors [(DataProcessor. "Proc1")
                      (DataProcessor. "Proc2")
                      (DataProcessor. "Proc3")]
          ;; Process same data with different processors
          results (map #(.ProcessData % "test") processors)]
      (is (= 3 (count results)) "Should have 3 results")
      (is (every? string? results) "All results should be strings")
      (is (every? #(.Contains % "TEST") results) "All should contain uppercase TEST"))
    
    ;; Test with Clojure data structures
    (let [people [(Person. "Alice" "A" 20)
                  (Person. "Bob" "B" 30)
                  (Person. "Charlie" "C" 40)]
          ages (map #(.Age %) people)
          avg-age (/ (reduce + ages) (count ages))]
      (is (= [20 30 40] ages) "Should extract ages correctly")
      (is (= 30 avg-age) "Average age should be 30"))))

(deftest test-data-processor-status
  (testing "DataProcessor GetStatus method"
    (let [processor (DataProcessor. "StatusTest")]
      ;; Process some data first
      (.ProcessData processor "item1")
      (.ProcessData processor "item2")
      
      (let [status (.GetStatus processor)]
        ;; Just verify it returns something and has expected keys
        (is (not (nil? status)) "Should return status object")
        (is (= "StatusTest" (.get_Item status "processor")) "Should have processor name")
        (is (= 2 (.get_Item status "count")) "Should have correct count")))))